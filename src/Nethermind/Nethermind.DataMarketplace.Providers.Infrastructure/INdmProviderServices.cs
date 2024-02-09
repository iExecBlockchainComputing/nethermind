// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Providers.Services;

namespace Nethermind.DataMarketplace.Providers.Infrastructure
{
    public interface INdmProviderServices
    {
        IProviderService ProviderService { get; }
    }
}