// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Events;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IProviderService
    {
        event EventHandler<AddressChangedEventArgs> AddressChanged;
        event EventHandler<AddressChangedEventArgs> ColdWalletAddressChanged;
        Address GetAddress();
        Address GetColdWalletAddress();
        Task ChangeAddressAsync(Address address);
        Task ChangeColdWalletAddressAsync(Address address);
        Task ChangeConsumerAddressAsync(INdmProviderPeer peer, Address address);
        Task<Consumer?> GetConsumerAsync(Keccak depositId);
        Task<PagedResult<Consumer>> GetConsumersAsync(GetConsumers query);
        Task<PagedResult<DataAsset>> GetDataAssetsAsync(GetDataAssets query);

        Task<Keccak?> AddDataAssetAsync(string name, string description, UInt256 unitPrice, DataAssetUnitType unitType,
            uint minUnits, uint maxUnits, DataAssetRules rules, string? file = null, byte[]? data = null,
            QueryType? queryType = null, string? termsAndConditions = null, bool kycRequired = false,
            string? plugin = null);

        Task<bool> RemoveDataAssetAsync(Keccak id);
        void AddConsumerPeer(INdmProviderPeer peer);
        Task StartSessionAsync(DataRequest dataRequest, uint consumedUnitsFromConsumer, INdmProviderPeer peer);
        Task FinishSessionAsync(Keccak depositId, INdmProviderPeer peer, bool removePeer = true);
        Task FinishSessionsAsync(INdmProviderPeer peer, bool removePeer = true);
        Task<bool> EnableDataStreamAsync(Keccak depositId, string client, string[] args, INdmProviderPeer peer);
        Task<bool> DisableDataStreamAsync(Keccak depositId, string client, INdmProviderPeer ndmPeer);
        Task SendDataAssetDataAsync(DataAssetData dataAssetData);
        Task<Keccak?> SendEarlyRefundTicketAsync(Keccak depositId, RefundReason? reason = RefundReason.DataDiscontinued);
        Task<bool> ChangeDataAssetStateAsync(Keccak assetId, DataAssetState state);
        Task<bool> ChangeDataAssetPluginAsync(Keccak assetId, string? plugin);
        Task<PagedResult<DepositApproval>> GetDepositApprovalsAsync(GetProviderDepositApprovals query);
        Task<Keccak?> RequestDepositApprovalAsync(Keccak assetId, Address consumer, string kyc);
        Task<Keccak?> ConfirmDepositApprovalAsync(Keccak assetId, Address consumer);
        Task<Keccak?> RejectDepositApprovalAsync(Keccak assetId, Address consumer);
        Task SendDepositApprovalsAsync(INdmProviderPeer peer, Keccak? dataAssetId = null, bool onlyPending = false);
        Task InitPluginAsync(INdmPlugin plugin);
        string[] GetPlugins();
    }
}
