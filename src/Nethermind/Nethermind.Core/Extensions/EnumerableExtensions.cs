// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nethermind.Core.Extensions
{
    public static class EnumerableExtensions
    {
        public static ISet<T> AsSet<T>(this IEnumerable<T> enumerable) =>
            enumerable is ISet<T> set ? set : enumerable.ToHashSet();

        public static string ToString<T>(this IEnumerable<T> enumerable)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('[');
            builder.AppendJoin(" ", enumerable);
            builder.Append(']');
            return builder.ToString();
        }
    }
}
