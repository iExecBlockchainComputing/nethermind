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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class BlockInfoDecoder : IRlpStreamDecoder<BlockInfo>, IRlpValueDecoder<BlockInfo>
    {
        public BlockInfo? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;

            Keccak? blockHash = rlpStream.DecodeKeccak();

            bool wasProcessed = rlpStream.DecodeBool();
            UInt256 totalDifficulty = rlpStream.DecodeUInt256();

            BlockMetadata metadata = BlockMetadata.None;
            // if we hadn't reached the end of the stream, assume we have metadata to decode
            if (rlpStream.Position != lastCheck)
            {
                metadata = (BlockMetadata)rlpStream.DecodeInt();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(lastCheck);
            }

            if (blockHash is null)
            {
                return null;
            }

            BlockInfo blockInfo = new(blockHash, totalDifficulty)
            {
                WasProcessed = wasProcessed,
                Metadata = metadata,
            };

            return blockInfo;
        }

        public void Encode(RlpStream stream, BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.Encode(Rlp.OfEmptySequence);
                return;
            }

            int contentLength = GetContentLength(item, rlpBehaviors);

            bool hasMetadata = item.Metadata != BlockMetadata.None;
            stream.StartSequence(contentLength);
            stream.Encode(item.BlockHash);
            stream.Encode(item.WasProcessed);
            stream.Encode(item.TotalDifficulty);
            if (hasMetadata)
            {
                stream.Encode((int)item.Metadata);
            }
        }

        public Rlp Encode(BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        private int GetContentLength(BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence.Length;
            }

            bool hasMetadata = item.Metadata != BlockMetadata.None;
            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.BlockHash);
            contentLength += Rlp.LengthOf(item.WasProcessed);
            contentLength += Rlp.LengthOf(item.TotalDifficulty);

            if (hasMetadata)
            {
                contentLength += Rlp.LengthOf((int)item.Metadata);
            }

            return contentLength;
        }

        public int GetLength(BlockInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }

        public BlockInfo? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;

            Keccak? blockHash = decoderContext.DecodeKeccak();
            bool wasProcessed = decoderContext.DecodeBool();
            UInt256 totalDifficulty = decoderContext.DecodeUInt256();

            BlockMetadata metadata = BlockMetadata.None;
            // if we hadn't reached the end of the stream, assume we have metadata to decode
            if (decoderContext.Position != lastCheck)
            {
                metadata = (BlockMetadata)decoderContext.DecodeInt();
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                decoderContext.Check(lastCheck);
            }

            if (blockHash is null)
            {
                return null;
            }

            BlockInfo blockInfo = new(blockHash, totalDifficulty)
            {
                WasProcessed = wasProcessed,
                Metadata = metadata
            };

            return blockInfo;
        }
    }
}
