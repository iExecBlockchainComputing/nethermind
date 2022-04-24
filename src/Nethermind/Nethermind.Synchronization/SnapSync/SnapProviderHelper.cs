using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    internal static class SnapProviderHelper
    {
        private static object _syncCommit = new();

        private static int _accCommitInProgress = 0;
        private static int _slotCommitInProgress = 0;

        public static (AddRangeResult result, bool moreChildrenToRight, IList<PathWithAccount> storageRoots, IList<Keccak> codeHashes) 
            AddAccountRange(StateTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            // TODO: Check the accounts boundaries and sorting

            //var rlps = proofs.Select(p => $"{Keccak.Compute(p).ToString(false)}:{new Rlp(p).ToString(false)}").ToArray();
            //var res = string.Join($"{Environment.NewLine}{Environment.NewLine}", rlps);
            //var first = proofs.Select((p) => { var n = (new TrieNode(NodeType.Unknown, p, true)); n.ResolveNode(tree.TrieStore); return n; }) ;


            Keccak lastHash = accounts.Last().Path;

            (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(tree, startingHash, lastHash, expectedRootHash, proofs);

            if(result != AddRangeResult.OK)
            {
                return (result, true, null, null);
            }

            IList<PathWithAccount> accountsWithStorage = new List<PathWithAccount>();
            IList<Keccak> codeHashes = new List<Keccak>();

            try
            {
                foreach (var account in accounts)
                {
                    if (account.Account.HasStorage)
                    {
                        accountsWithStorage.Add(account);
                    }

                    if (account.Account.HasCode)
                    {
                        codeHashes.Add(account.Account.CodeHash);
                    }

                    tree.Set(account.Path, account.Account);
                }
            }
            catch (Exception)
            {               
                throw;
            }

            tree.UpdateRootHash();

            if (tree.RootHash != expectedRootHash)
            {
                return (AddRangeResult.DifferentRootHash, true, null, null);
            }

            try
            {
                Interlocked.Exchange(ref _accCommitInProgress, 1);

                StitchBoundaries(sortedBoundaryList, tree.TrieStore);

                lock (_syncCommit)
                {
                    tree.Commit(blockNumber);
                }

                Interlocked.Exchange(ref _accCommitInProgress, 0);
            }
            catch (Exception ex)
            {

                throw new Exception($"{ex.Message}, _accCommitInProgress:{_accCommitInProgress}, _slotCommitInProgress:{_slotCommitInProgress}", ex);
            }

            return (AddRangeResult.OK, moreChildrenToRight, accountsWithStorage, codeHashes);
        }

        public static (AddRangeResult result, bool moreChildrenToRight) AddStorageRange(StorageTree tree, long blockNumber, Keccak startingHash, PathWithStorageSlot[] slots, Keccak expectedRootHash, byte[][] proofs = null)
        {
            // TODO: Check the slots boundaries and sorting

            Keccak lastHash = slots.Last().Path;

            (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(tree, startingHash, lastHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return (result, true);
            }

            // TODO: try-catch not needed, just for debug
            try
            {
                foreach (var slot in slots)
                {
                    tree.Set(slot.Path, slot.SlotRlpValue, false);
                }
            }
            catch (Exception)
            {
                throw;
            }

            tree.UpdateRootHash();

            if (tree.RootHash != expectedRootHash)
            {
                return (AddRangeResult.DifferentRootHash, true);
            }

            try
            {
                Interlocked.Exchange(ref _slotCommitInProgress, 1);

                StitchBoundaries(sortedBoundaryList, tree.TrieStore);

                lock (_syncCommit)
                {
                    tree.Commit(blockNumber);
                }

                Interlocked.Exchange(ref _slotCommitInProgress, 0);
            }
            catch (Exception ex)
            {
                throw new Exception($"{ex.Message}, _accCommitInProgress:{_accCommitInProgress}, _slotCommitInProgress:{_slotCommitInProgress}", ex);
            }

            return (AddRangeResult.OK, moreChildrenToRight);
        }

        private static (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) FillBoundaryTree(PatriciaTree tree, Keccak startingHash, Keccak endHash, Keccak expectedRootHash, byte[][] proofs = null)
        {
            if (proofs is null || proofs.Length == 0)
            {
                return (AddRangeResult.OK, null, false);
            }

            if (tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            startingHash ??= Keccak.Zero;
            List<TrieNode> sortedBoundaryList = new();

            Dictionary<Keccak, TrieNode> dict = CreateProofDict(proofs, tree.TrieStore);

            if(!dict.TryGetValue(expectedRootHash, out TrieNode root))
            {
                return (AddRangeResult.MissingRootHashInProofs, null, true);
            }

            Keccak rootHash = new Keccak(root.Keccak.Bytes);

            Dictionary<Keccak, TrieNode> processed = new();
            Span<byte> leftBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(startingHash.Bytes, leftBoundary);
            Span<byte> rightBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(endHash.Bytes, rightBoundary);

            Stack<(TrieNode parent, TrieNode node, int pathIndex, List<byte> path)> proofNodesToProcess = new();

            tree.RootRef = root;
            proofNodesToProcess.Push((null, root, -1, new List<byte>()));
            sortedBoundaryList.Add(root); ;

            bool moreChildrenToRight = false;

            while (proofNodesToProcess.Count > 0)
            {
                (TrieNode parent, TrieNode node, int pathIndex, List<byte> path) = proofNodesToProcess.Pop();

                if (node.IsExtension)
                {
                    Keccak? childKeccak = node.GetChildHash(0);

                    if (childKeccak is not null)
                    {
                        if (dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            node.SetChild(0, child);

                            pathIndex += node.Path.Length;
                            path.AddRange(node.Path);
                            proofNodesToProcess.Push((node, child, pathIndex, path));
                            sortedBoundaryList.Add(child);
                        }
                        else
                        {
                            if (Bytes.Comparer.Compare(path.ToArray(), leftBoundary[0..path.Count()].ToArray()) >= 0
                                && parent is not null
                                && parent.IsBranch)
                            {
                                for (int i = 0; i < 15; i++)
                                {
                                    Keccak? kec = parent.GetChildHash(i);
                                    if (kec == node.Keccak)
                                    {
                                        parent.SetChild(i, null);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                if (node.IsBranch)
                {
                    pathIndex++;

                    int left = Bytes.Comparer.Compare(path.ToArray(), leftBoundary[0..path.Count()].ToArray()) == 0 ? leftBoundary[pathIndex] : 0;
                    int right = Bytes.Comparer.Compare(path.ToArray(), rightBoundary[0..path.Count()].ToArray()) == 0 ? rightBoundary[pathIndex] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        Keccak? childKeccak = node.GetChildHash(ci);

                        moreChildrenToRight |= ci > right && childKeccak is not null;

                        if (ci >= left && ci <= right)
                        {
                            node.SetChild(ci, null);
                        }

                        if (childKeccak is not null && (ci == left || ci == right) && dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            if (!child.IsLeaf)
                            {
                                node.SetChild(ci, child);

                                // TODO: we should optimize it - copy only if there are two boundary children
                                List<byte> newPath = new(path);

                                newPath.Add((byte)ci);

                                proofNodesToProcess.Push((node, child, pathIndex, newPath));
                                sortedBoundaryList.Add(child);
                            }
                        }
                    }
                }
            }

            return (AddRangeResult.OK, sortedBoundaryList, moreChildrenToRight);
        }

        private static Dictionary<Keccak, TrieNode> CreateProofDict(byte[][] proofs, ITrieStore store)
        {
            Dictionary<Keccak, TrieNode> dict = new();

            for (int i = 0; i < proofs.Length; i++)
            {
                byte[] proof = proofs[i];
                var node = new TrieNode(NodeType.Unknown, proof, true);
                node.IsBoundaryProofNode = true;
                node.ResolveNode(store);
                node.ResolveKey(store, i == 0);

                dict[node.Keccak] = node;
            }

            return dict;
        }

        private static void StitchBoundaries(IList<TrieNode> sortedBoundaryList, ITrieStore store)
        {
            if (sortedBoundaryList == null || sortedBoundaryList.Count == 0)
            {
                return;
            }

            for (int i = sortedBoundaryList.Count - 1; i >= 0; i--)
            {
                TrieNode node = sortedBoundaryList[i];

                if (!node.IsPersisted)
                {
                    if (node.IsExtension)
                    {
                        if (IsChildPersisted(node, 1, store))
                        {
                            node.IsBoundaryProofNode = false;
                        }
                    }

                    if (node.IsBranch)
                    {
                        bool isBoundaryProofNode = false;
                        for (int ci = 0; ci <= 15; ci++)
                        {
                            if (!IsChildPersisted(node, ci, store))
                            {
                                isBoundaryProofNode = true;
                                break;
                            }
                        }

                        node.IsBoundaryProofNode = isBoundaryProofNode;
                    }
                }
            }
        }

        private static bool IsChildPersisted(TrieNode node, int childIndex, ITrieStore store)
        {
            TrieNode data = node.GetData(childIndex) as TrieNode;
            if (data != null)
            {

                return data.IsBoundaryProofNode == false;
            }

            Keccak childKeccak = node.GetChildHash(childIndex);
            if (childKeccak is null)
            {
                return true;
            }

            return store.IsPersisted(childKeccak);
        }
    }
}