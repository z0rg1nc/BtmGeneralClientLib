using System;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.CdnProxyEndReceiverClientTransport;
using BtmI2p.GeneralClientInterfaces;
using BtmI2p.GeneralClientInterfaces.CdnProxyServer;
using BtmI2p.GeneralClientInterfaces.LookupServer;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.BitMoneyClient.Lib
{
    public class LookupServerSession : IMyAsyncDisposable
    {
        private LookupServerSession()
        {
        }

        public async Task<ServerAddressForClientInfo> GetUserServerAddress(
            Guid userGuid,
            CancellationToken cancellationToken
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        return await _lookupInterface.GetUserServerAddress(
                            userGuid
                        ).ThrowIfCancelled(_cts.Token)
                        .ThrowIfCancelled(cancellationToken)
                        .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        public async Task<ServerAddressForClientInfo> GetWalletServerAddress(
            Guid walletGuid,
            CancellationToken cancellationToken
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        return await _lookupInterface.GetWalletServerAddress(
                            walletGuid
                        ).ThrowIfCancelled(_cts.Token)
                        .ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        public async Task<ServerAddressForClientInfo> GetExchangeServerAddress(
            Guid exchangeClientGuid,
            CancellationToken cancellationToken
            )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                while (true)
                {
                    try
                    {
                        return await _lookupInterface.GetExchangeServerAddress(
                            exchangeClientGuid
                        ).ThrowIfCancelled(_cts.Token)
                        .ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        public async Task<ServerAddressForClientInfo> GetMiningServerAddress(
            Guid miningClientGuid,
            CancellationToken cancellationToken
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                return await MiscFuncs.RepeatWhileTimeout(
                    async () => await _lookupInterface.GetMiningServerAddress(
                        miningClientGuid
                    ).ConfigureAwait(false),
                    _cts.Token,
                    cancellationToken
                ).ConfigureAwait(false);
            }
        }
        private IFromClientToLookup _lookupInterface = null;
        private IProxyServerSession _proxySession;
        public static async Task<LookupServerSession> CreateInstance(
            IProxyServerSession proxySession,
            CancellationToken cancellationToken
        )
        {
            var result = new LookupServerSession();
            result._proxySession = proxySession;
            ServerAddressForClientInfo lookupServerId;
            while (true)
            {
                try
                {
                    lookupServerId =
                        await proxySession
                            .ProxyInterface
                            .GetLookupEndReceiverIdCheckVersion(
                                new VersionCompatibilityRequest()
                            ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                }
            }
            result._lookupInterface = await EndReceiverServiceClientInterceptor
                .GetClientProxy<IFromClientToLookup>(
                    proxySession.ProxyInterface,
                    lookupServerId.ServerGuid,
                    cancellationToken
                ).ConfigureAwait(false);
            result._stateHelper.SetInitializedState();
            return await Task.FromResult(result).ConfigureAwait(false);
        }

        private readonly DisposableObjectStateHelper _stateHelper 
            = new DisposableObjectStateHelper("LookupServerSession");
        private readonly CancellationTokenSource _cts 
            = new CancellationTokenSource();
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
