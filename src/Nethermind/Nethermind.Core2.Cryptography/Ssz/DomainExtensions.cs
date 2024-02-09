// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Cortex.SimpleSerialize;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class DomainExtensions
    {
        public static SszElement ToSszBasicVector(this Domain item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}