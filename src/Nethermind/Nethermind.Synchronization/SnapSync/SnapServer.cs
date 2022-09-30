//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class SnapServer: ISnapServer
{
    private readonly ITrieStore _store;
    private readonly IDbProvider _dbProvider;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private readonly AccountDecoder _decoder = new();

    public SnapServer(IDbProvider dbProvider, ILogManager logManager)
    {
        _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
        _store = new TrieStore(
            _dbProvider.StateDb,
            logManager);

        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();
    }


    public byte[][]?  GetTrieNodes(PathGroup[] pathSet, Keccak rootHash)
    {
        // TODO: use cache to reduce node retrieval from disk
        int pathLength = pathSet.Length;
        List<byte[]> response = new ();
        StateTree tree = new(_store, _logManager);

        for (int reqi = 0; reqi < pathLength; reqi++)
        {
            var requestedPath = pathSet[reqi].Group;
            switch (requestedPath.Length)
            {
                case 0:
                    return null;
                case 1:
                    var rlp = tree.GetNode(requestedPath[0], rootHash);
                    response.Add(rlp);
                    break;
                default:
                    byte[]? accBytes = tree.GetNode(requestedPath[0], rootHash);
                    if (accBytes is null)
                    {
                        // TODO: how to deal with empty account when storage asked?
                        response.Add(null);
                        continue;
                    }
                    Account? account = _decoder.Decode(accBytes.AsRlpStream());
                    var storageRoot = account.StorageRoot;
                    StorageTree sTree = new StorageTree(_store, storageRoot, _logManager);

                    for (int reqStorage = 1; reqStorage < requestedPath.Length; reqStorage++)
                    {
                        var sRlp = sTree.GetNode(requestedPath[reqStorage]);
                        response.Add(sRlp);
                    }
                    break;
            }
        }

        return response.ToArray();
    }

    public byte[][] GetByteCodes(Keccak[] requestedHashes, long byteLimit)
    {
        long currentByteCount = 0;
        List<byte[]> response = new ();

        foreach (Keccak codeHash in requestedHashes)
        {
            // break when the response size exceeds the byteLimit - it is a soft limit
            // so not a big issue if we so over slightly.
            if (currentByteCount > byteLimit)
            {
                break;
            }

            // TODO: handle empty code in the DB itself?
            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                response.Add(new byte[] { });
            }
            byte[]? code = _dbProvider.CodeDb.Get(codeHash);
            if (code is null) continue;
            response.Add(code);
            currentByteCount += code.Length;
        }

        return response.ToArray();
    }

    public (PathWithAccount[], byte[][]) GetAccountRanges(Keccak rootHash, Keccak startingHash, Keccak? limitHash, long byteLimit)
    {

        (object[]? accountNodes, long _, bool _) = GetNodesFromTrieVisitor(rootHash, startingHash, limitHash == null ? Keccak.MaxValue : limitHash, byteLimit);
        StateTree tree = new(_store, _logManager);
        PathWithAccount[] nodes = (PathWithAccount[]) accountNodes;

        // TODO: add error handling when proof is null
        AccountProofCollector accountProofCollector = new(nodes[0].Path.Bytes);
        tree.Accept(accountProofCollector, rootHash);
        byte[][] firstProof = accountProofCollector.BuildResult().Proof;

        // TODO: add error handling when proof is null
        accountProofCollector = new AccountProofCollector(nodes[^1].Path.Bytes);
        tree.Accept(accountProofCollector, rootHash);
        byte[][] lastProof = accountProofCollector.BuildResult().Proof;

        List<byte[]> proofs = new();
        proofs.AddRange(firstProof);
        proofs.AddRange(lastProof);
        // byte[][] proofs = new byte[firstProof.Length + lastProof.Length][];
        // Buffer.BlockCopy(firstProof, 0, proofs, 0, firstProof.Length);
        // Buffer.BlockCopy(lastProof, 0, proofs, firstProof.Length, lastProof.Length);
        return (nodes, proofs.ToArray());
    }

    public (PathWithStorageSlot[][], byte[][]?) GetStorageRanges(Keccak rootHash, PathWithAccount[] accounts, Keccak? startingHash, Keccak? limitHash, long byteLimit)
    {
        long responseSize = 0;
        StateTree tree = new(_store, _logManager);
        List <PathWithStorageSlot[]> responseNodes = new();
        for (int i = 0; i < accounts.Length; i++)
        {
            if (responseSize > byteLimit)
            {
                break;
            }
            // TODO: is it a good idea to get storage root from here?
            var storageRoot = accounts[i].Account.StorageRoot;

            // TODO: handle null values in the node visitor - will be more efficient.
            startingHash = startingHash == null ? Keccak.Zero : startingHash;
            limitHash = limitHash == null ? Keccak.MaxValue : limitHash;

            (object []? storageNodes, long innerResponseSize, bool stopped) = GetNodesFromTrieVisitor(storageRoot, startingHash, limitHash, byteLimit - responseSize);
            PathWithStorageSlot[] nodes = (PathWithStorageSlot[]) storageNodes;
            responseNodes.Add(nodes);
            if (stopped || startingHash != Keccak.Zero)
            {
                // generate proof
                // TODO: add error handling when proof is null
                AccountProofCollector accountProofCollector = new(nodes[0].Path.Bytes);
                tree.Accept(accountProofCollector, storageRoot);
                byte[][]? firstProof = accountProofCollector.BuildResult().Proof;

                // TODO: add error handling when proof is null
                accountProofCollector = new AccountProofCollector(nodes[^1].Path.Bytes);
                tree.Accept(accountProofCollector, storageRoot);
                byte[][]? lastProof = accountProofCollector.BuildResult().Proof;

                List<byte[]> proofs = new();
                proofs.AddRange(firstProof);
                proofs.AddRange(lastProof);
                // byte[][] proofs = new byte[firstProof.Length + lastProof.Length][];
                // Buffer.BlockCopy(firstProof, 0, proofs, 0, firstProof.Length);
                // Buffer.BlockCopy(lastProof, 0, proofs, firstProof.Length, lastProof.Length);
                return (responseNodes.ToArray(), proofs.ToArray());
            }
            responseSize += innerResponseSize;
        }
        return (responseNodes.ToArray(), null);
    }


    // this is a very bad idea
    private (PathWithNode[], long, bool) GetNodesFromTrieBruteForce(Keccak rootHash, Keccak startingHash, Keccak limitHash, long byteLimit)
    {
        // TODO: incase of storage trie its preferable to get the complete node - so this byteLimit should be a hard limit

        long responseSize = 0;
        bool stopped = false;
        PatriciaTree tree = new(_store, _logManager);
        List<PathWithNode> nodes = new ();

        UInt256 startHashNum = new(startingHash.Bytes, true);
        UInt256 endHashNum = new(limitHash.Bytes, true);
        UInt256 itr = startHashNum;
        Span<byte> key = itr.ToBigEndian();

        while (true)
        {
            if (itr > endHashNum)
            {
                break;
            }

            if (responseSize > byteLimit)
            {
                stopped = true;
                break;
            }

            itr.ToBigEndian(key);

            var blob = tree.GetNode(key, rootHash);

            if (blob is not null)
            {
                PathWithNode result = new(key.ToArray(), blob);
                nodes.Add(result);
                responseSize += 32 + blob.Length;
            }
            itr++;
        }

        return (nodes.ToArray(), responseSize, stopped);
    }

    private (object[], long, bool) GetNodesFromTrieVisitor(Keccak rootHash, Keccak startingHash, Keccak limitHash,
        long byteLimit, bool isStorage=false)

    {
        // TODO: incase of storage trie its preferable to get the complete node - so this byteLimit should be a hard limit

        long responseSize = 0;
        bool stopped = false;
        PatriciaTree tree = new(_store, _logManager);


        RangeQueryVisitor visitor = new(startingHash.Bytes, limitHash.Bytes, byteLimit);
        VisitingOptions opt = new() {ExpectAccounts = false, KeepTrackOfAbsolutePath = true};
        tree.Accept(visitor, rootHash, opt);
        Dictionary<byte[], byte[]>? requiredNodes = visitor.GetNodes();

        if (isStorage)
        {
            PathWithStorageSlot[] nodes = new PathWithStorageSlot[requiredNodes.Count];
            int i = 0;
            foreach (PathWithStorageSlot? result in requiredNodes.Select(res => new PathWithStorageSlot(new Keccak(res.Key), res.Value)))
            {
                nodes[i] = result;
                i += 1;
            }
            return (nodes.ToArray(), responseSize, stopped);
        }
        else
        {
            PathWithAccount[] nodes = new PathWithAccount[requiredNodes.Count];
            int i = 0;
            foreach (PathWithAccount? result in requiredNodes.Select(res => new PathWithAccount(new Keccak(res.Key), _decoder.Decode(new RlpStream(res.Value)))))
            {
                nodes[i] = result;
                i += 1;
            }
            return (nodes.ToArray(), responseSize, stopped);
        }



    }

}