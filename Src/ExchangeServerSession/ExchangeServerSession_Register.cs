using System;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.CdnProxyEndReceiverClientTransport;
using BtmI2p.GeneralClientInterfaces.ExchangeServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;

namespace BtmI2p.BitMoneyClient.Lib.ExchangeServerSession
{
    public partial class ExchangeServerSession
    {
        public static async Task<LightCertificate> RegisterExchangeClient(
            IProxyServerSession proxySession,
            LookupServerSession lookupSession,
            AesProtectedByteArray newExchangeClientCertPass,
            CancellationToken cancellationToken
        )
        {
            var exchangeServerInfo
                = await lookupSession.GetExchangeServerAddress(
                    MiscFuncs.GenGuidWithFirstBytes(0),
                    cancellationToken
                ).ConfigureAwait(false);
            if (exchangeServerInfo.ServerGuid == Guid.Empty)
                throw new Exception("Exchange server info not found");
            var exchangeServerInterface
                = await EndReceiverServiceClientInterceptor
                    .GetClientProxy<IFromClientToExchange>(
                        proxySession.ProxyInterface,
                        exchangeServerInfo.ServerGuid,
                        cancellationToken,
                        exchangeServerInfo.EndReceiverMethodInfos
                    ).ConfigureAwait(false);
            Guid newExchangeClientGuid;
            while (true)
            {
                try
                {
                    newExchangeClientGuid =
                        await
                            exchangeServerInterface.GenExchangeClientGuidForRegistration()
                                .ThrowIfCancelled(cancellationToken)
                                .ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                }
            }
            LightCertificate newExchangeClientCert;
            using (var tempData = newExchangeClientCertPass.TempData)
            {
                var exchangeClientCertPassBytes = tempData.Data;
                newExchangeClientCert =
                    LightCertificatesHelper.GenerateSelfSignedCertificate(
                        ELightCertificateSignType.Rsa,
                        2048,
                        ELightCertificateEncryptType.Rsa,
                        2048,
                        EPrivateKeysKeyDerivationFunction.ScryptDefault,
                        newExchangeClientGuid,
                        newExchangeClientGuid.ToString(),
                        exchangeClientCertPassBytes
                    );
            }
            var requestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
            while (true)
            {
                try
                {
                    var request = new RegisterExchangeClientCertRequest()
                    {
                        PublicExchangeClientCert = newExchangeClientCert.GetOnlyPublic(),
                        RequestGuid = requestGuid,
                        SentTime = await proxySession.GetNowTime().ConfigureAwait(false)
                    };
                    SignedData<RegisterExchangeClientCertRequest> signedRequest;
                    using (var tempPass = newExchangeClientCertPass.TempData)
                    {
                        signedRequest =
                            new SignedData<RegisterExchangeClientCertRequest>(
                                request,
                                newExchangeClientCert,
                                tempPass.Data
                                );
                    }
                    await exchangeServerInterface.RegisterExchangeClientCert(
                        signedRequest
                        ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (RpcRethrowableException rpcExc)
                {
                    if (
                        rpcExc.ErrorData.ErrorCode
                        == (int)ERegisterExchangeClientCertErrCodes.AlreadyRegistered
                        )
                        break;
                    throw;
                }
                catch (TimeoutException)
                {
                }
            }
            return newExchangeClientCert;
        }
    }
}
