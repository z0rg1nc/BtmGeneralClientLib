using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.GeneralClientInterfaces.WalletServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.WalletServerSession
{
    public partial class WalletServerSession
    {
	    public enum ESendTransferErrCodes
	    {
		    WrongCertPass
	    }

        /// <returns>Request guid</returns>
        /// <exception cref="EnumException{ESendTransferErrCodes}"></exception>
        public async Task<Guid> SendTransfer(
            AesProtectedByteArray walletCertPass,
            Guid walletTo,
            long amount,
            byte[] commentBytes,
            bool encryptComment = true,
            bool anonymousTransfer = false,
            Guid requestGuid = default(Guid), //Guid.Empty
            bool suppressCheckPassword = false,
            long maxFee = 0
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                if (requestGuid == Guid.Empty)
                    requestGuid = Guid.NewGuid();
                if (!suppressCheckPassword)
                {
                    using (var curPass = _walletCertPass.TempData)
                    {
                        using (var testedPass = walletCertPass.TempData)
                        {
                            if (!curPass.Data.SequenceEqual(testedPass.Data))
                                throw EnumException.Create(
									ESendTransferErrCodes.WrongCertPass
								);
                        }
                    }
                }
                PreparedToSendTransfer newPreparedTransfer;
                using (await _preparedToSendTransfersLockSem.GetDisposable().ConfigureAwait(false))
                {
                    newPreparedTransfer = new PreparedToSendTransfer()
                    {
                        CommentBytes = commentBytes,
                        EncryptComment = encryptComment,
                        WalletTo = walletTo,
                        Amount = amount,
                        AnonymousTransfer = anonymousTransfer,
                        RequestGuid = requestGuid,
                        MaxFee = maxFee
                    };
                    _preparedToSendTransfers.Add(
                        newPreparedTransfer
                    );
                }
                _walletSessionModel.OnPreparedToSendTransferAdded.OnNext(newPreparedTransfer);
                return requestGuid;
            }
        }
        // WalletTo, KeyInfo
        private readonly ConcurrentDictionary<Guid, WalletCommentKeyClientInfo> _cachedActiveOutCommentKeyInfoDb
            = new ConcurrentDictionary<Guid, WalletCommentKeyClientInfo>();
        // KeyGuid -> aesPair
        private readonly ConcurrentDictionary<Guid, MutableTuple<AesKeyIvPair,bool>> _outCommentKeyDb
            = new ConcurrentDictionary<Guid, MutableTuple<AesKeyIvPair,bool>>();
        private readonly SemaphoreSlim _onPreparedSendTransferAddedActionLockSem = new SemaphoreSlim(1);
        private async void OnPreparedSendTransferAddedAction(
            PreparedToSendTransfer preparedTransfer
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await _onPreparedSendTransferAddedActionLockSem.GetDisposable(token: _cts.Token).ConfigureAwait(false))
                    {
                        var isWalletToRegistered = await MiscFuncs.RepeatWhileTimeout(
                            async () => await _walletTransport.IsWalletRegistered(
                                preparedTransfer.WalletTo
                                ).ConfigureAwait(false),
                            _cts.Token
                            ).ConfigureAwait(false);
                        if (!isWalletToRegistered)
                        {
                            using (await _preparedToSendTransfersLockSem.GetDisposable().ConfigureAwait(false))
                            {
                                _preparedToSendTransfers.RemoveAll(
                                    x => x.RequestGuid == preparedTransfer.RequestGuid
                                );
                            }
                            _walletSessionModel
                                .OnPreparedToSendTransferFault
                                .OnNext(
                                    new OnPreparedToSendTransferFaultArgs()
                                    {
                                        RequestGuid = preparedTransfer.RequestGuid,
                                        PreparedTransfer = preparedTransfer,
                                        FaultCode = EPreparedToSendTransferFaultErrCodes.WalletToNotExist,
                                        FaultMessage = $"{EPreparedToSendTransferFaultErrCodes.WalletToNotExist}"
                                    }
                                );
                            return;
                        }
                        if(preparedTransfer.EncryptComment)
                            await UpdateOtherWalletCertInfoPrivate(
                                new[] {preparedTransfer.WalletTo}
                            ).ConfigureAwait(false);
                        byte[] origCommentBody = preparedTransfer.CommentBytes;
                        Guid commentKeyGuid;
                        byte[] commentBody;
                        var signatureType = ETransferSignatureType.None;
                        Func<byte[],byte[]> getTransferSignatureFunc = _ => new byte[0];
                        if (preparedTransfer.EncryptComment)
                        {
                            AesKeyIvPair commentAesKey;
                            WalletCommentKeyClientInfo walletCommentKeyInfo;
                            if (preparedTransfer.AnonymousTransfer)
                            {
                                walletCommentKeyInfo = await RegisterNewOutKey(
                                    preparedTransfer.WalletTo,
                                    true
                                    ).ConfigureAwait(false);
                            }
                            else
                            {
                                while (true)
                                {
                                    if (
                                        !_cachedActiveOutCommentKeyInfoDb.ContainsKey(
                                            preparedTransfer.WalletTo
                                            )
                                        )
                                    {
                                        var outKeyList = await MiscFuncs.RepeatWhileTimeout(
                                            async () => await _authedWalletInterface
                                                .GetValidCommentOutKeyList(
                                                    preparedTransfer.WalletTo
                                                ).ConfigureAwait(false),
                                            _cts.Token
                                            ).ConfigureAwait(false);
                                        if (outKeyList.Count == 0)
                                        {
                                            walletCommentKeyInfo = await RegisterNewOutKey(
                                                preparedTransfer.WalletTo,
                                                false
                                                ).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            walletCommentKeyInfo =
                                                outKeyList.OrderByDescending(
                                                    x => x.ValidUntilTime
                                                    ).First();
                                        }
                                        _cachedActiveOutCommentKeyInfoDb.TryAdd(
                                            preparedTransfer.WalletTo,
                                            walletCommentKeyInfo
                                            );
                                    }
                                    else
                                    {
                                        walletCommentKeyInfo =
                                            _cachedActiveOutCommentKeyInfoDb[
                                                preparedTransfer.WalletTo
                                                ];
                                        if (
                                            walletCommentKeyInfo.ValidUntilTime
                                            <=
                                            await _getRelativeTimeInterface.GetNowTime().ConfigureAwait(false)
                                            - TimeSpan.FromMinutes(3.0d)
                                            )
                                        {
                                            WalletCommentKeyClientInfo removedWalletCommentKeyInfo;
                                            _cachedActiveOutCommentKeyInfoDb.TryRemove(
                                                preparedTransfer.WalletTo, out removedWalletCommentKeyInfo
                                                );
                                            continue;
                                        }
                                    }
                                    break;
                                }
                            }
                            if (_outCommentKeyDb.ContainsKey(walletCommentKeyInfo.CommentKeyGuid))
                            {
                                commentAesKey = _outCommentKeyDb[walletCommentKeyInfo.CommentKeyGuid].Item1;
                            }
                            else
                            {
                                using (var tempPass = _walletCertPass.TempData)
                                {
                                    commentAesKey = LightCertificatesHelper.DecryptAesKeyIvPair(
                                        walletCommentKeyInfo.KeyEncryptedFrom,
                                        _settings.WalletCert,
                                        tempPass.Data
                                        );
                                }
                                bool authenticatedKey;
                                if (walletCommentKeyInfo.SignatureType == EWalletCommentKeySignatureType.None)
                                    authenticatedKey = false;
                                else
                                {
                                    authenticatedKey = _settings.WalletCert.VerifyData(
                                        new SignedData()
                                        {
                                            Signature = walletCommentKeyInfo.Signature,
                                            Data = walletCommentKeyInfo.GetDataForSignature(
                                                commentAesKey.ToBinaryArray()
                                                )
                                        }
                                        );
                                }
                                _outCommentKeyDb.TryAdd(
                                    walletCommentKeyInfo.CommentKeyGuid,
                                    MutableTuple.Create(
                                        commentAesKey,
                                        authenticatedKey
                                        )
                                    );
                            }
                            commentKeyGuid = walletCommentKeyInfo.CommentKeyGuid;
                            commentBody = commentAesKey.EncryptData(origCommentBody);
                            if (!preparedTransfer.AnonymousTransfer)
                            {
                                getTransferSignatureFunc = _ => commentAesKey.EncryptData(_);
                                signatureType = ETransferSignatureType.Type20150929;
                            }
                        }
                        else
                        {
                            commentKeyGuid = Guid.Empty;
                            commentBody = origCommentBody;
                        }
                        /**/
                        var transferGuid = MiscFuncs.GenRandMaskedGuid(
                            _serverInfo.TransferAndCommentKeyGuidMask,
                            _serverInfo.TransferAndCommentKeyGuidMaskEqual
                        );
                        var transferToInfo = new TransferToInfo()
                        {
                            CommentKeyGuid = commentKeyGuid,
                            Comment = commentBody,
                            TransferAmount = preparedTransfer.Amount,
                            WalletToGuid = preparedTransfer.WalletTo,
                            AnonymousTransfer = preparedTransfer.AnonymousTransfer,
                            TransferGuid = transferGuid,
                            SignatureType = signatureType,
                            EncryptedSignature = new byte[0]
                        };
                        while (true)
                        {
                            var sentTime = MiscFuncs.RoundDateTimeToSeconds(
                                await _getRelativeTimeInterface.GetNowTime().ConfigureAwait(false)
                            );
                            if (signatureType != ETransferSignatureType.None)
                            {
                                transferToInfo.EncryptedSignature = getTransferSignatureFunc(
                                    transferToInfo.GetDataToSign(
                                        _settings.WalletCert.Id,
                                        sentTime
                                    )
                                );
                            }
                            var sendTransferRequest = new SimpleTransferOrderRequest()
                            {
                                TransferToInfos = new List<TransferToInfo>()
                                {
                                    transferToInfo
                                },
                                RequestGuid = preparedTransfer.RequestGuid,
                                SentTime = sentTime,
                                MaxFee = preparedTransfer.MaxFee
                            };
                            /**/
                            try
                            {
                                await _signedWalletTransportInterface.ProcessSimpleTransfer(
                                    sendTransferRequest
                                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                _walletSessionModel
                                    .OnPreparedToSendTransferComplete
                                    .OnNext(
                                        preparedTransfer.RequestGuid
                                    );
                                break;
                            }
                            catch (TimeoutException)
                            {
                            }
                            catch (RpcRethrowableException rpcExc)
                            {
                                var errGeneralServerCode = EWalletGeneralErrCodes.NoErrors;
                                var errProcessTransferCode = EProcessSimpleTransferErrCodes.NoErrors;
                                if (Enum.IsDefined(typeof (EWalletGeneralErrCodes), rpcExc.ErrorData.ErrorCode))
                                    errGeneralServerCode = (EWalletGeneralErrCodes) rpcExc.ErrorData.ErrorCode;
                                else if (Enum.IsDefined(typeof (EProcessSimpleTransferErrCodes),
                                    rpcExc.ErrorData.ErrorCode))
                                    errProcessTransferCode =
                                        (EProcessSimpleTransferErrCodes) rpcExc.ErrorData.ErrorCode;
                                _walletSessionModel
                                    .OnPreparedToSendTransferFault
                                    .OnNext(
                                        new OnPreparedToSendTransferFaultArgs()
                                        {
                                            RequestGuid = preparedTransfer.RequestGuid,
                                            PreparedTransfer = preparedTransfer,
                                            FaultCode = EPreparedToSendTransferFaultErrCodes.ServerException,
                                            FaultMessage =
                                                errProcessTransferCode != EProcessSimpleTransferErrCodes.NoErrors
                                                    ? $"{errProcessTransferCode}"
                                                    : $"{errGeneralServerCode}",
                                            ServerFaultCode = errProcessTransferCode,
                                            ServerGeneralFaultCode = errGeneralServerCode
                                        }
                                    );
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception exc)
                            {
                                _walletSessionModel
                                    .OnPreparedToSendTransferFault
                                    .OnNext(
                                        new OnPreparedToSendTransferFaultArgs()
                                        {
                                            RequestGuid = preparedTransfer.RequestGuid,
                                            PreparedTransfer = preparedTransfer,
                                            FaultCode = EPreparedToSendTransferFaultErrCodes.UnknownError,
                                            FaultMessage = exc.Message
                                        }
                                    );
                                break;
                            }
                        }
                        /**/
                        using (await _preparedToSendTransfersLockSem.GetDisposable().ConfigureAwait(false))
                        {
                            _preparedToSendTransfers.RemoveAll(
                                x => x.RequestGuid == preparedTransfer.RequestGuid
                                );
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                MiscFuncs.HandleUnexpectedError(exc,_log);
            }
        }
        private readonly ConcurrentDictionary<Guid, ClientTransferBase> _outcomeTransfersDb
            = new ConcurrentDictionary<Guid, ClientTransferBase>();

        public async Task<Tuple<Guid, List<ClientTransferBase>>>
            GetOutcomeTransfersChunk(
                DateTime fromTime,
                DateTime toTime,
                Guid lastKnownReceivedTransferGuid,
                CancellationToken token,
                List<Guid> includeTransferGuidOnlyList = null
            )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                if(includeTransferGuidOnlyList == null)
                    includeTransferGuidOnlyList = new List<Guid>();
                var outTransferList = await MiscFuncs.RepeatWhileTimeout(
                    async () =>
                    {
                        var request = new GetSentTransfersOrderRequest()
                        {
                            DateTimeTo = toTime,
                            DateTimeFrom = fromTime,
                            LimitCount = _settings.BufferCount,
                            WalletTo = Guid.Empty,
                            LastKnownTransactionGuid = lastKnownReceivedTransferGuid,
                            IncludeTransferGuidOnlyList = includeTransferGuidOnlyList
                        };
                        request.CheckMe();
                        return await _authedWalletInterface.GetSentTransferInfo(
                            request
                        ).ConfigureAwait(false);
                    },
                    _cts.Token,
                    token
                ).ConfigureAwait(false);
                /* Update out certs */
                await UpdateOtherWalletCertInfoPrivate(
                    outTransferList
                        .Select(_ => _.WalletTo)
                        .ToList()
                ).ConfigureAwait(false);
                /**/
                var commentKeysToDownload = outTransferList
                    .Select(x => x.CommentKeyGuid)
                    .Where(x => x != Guid.Empty && !_outCommentKeyDb.ContainsKey(x))
                    .Distinct().ToList();
                if (commentKeysToDownload.Any())
                {
                    var newCommentOutKeys = await MiscFuncs.RepeatWhileTimeout(
                        async () => await _authedWalletInterface.GetCommentOutKeyList(
                            commentKeysToDownload
                        ).ConfigureAwait(false),
                        _cts.Token,
                        token
                    ).ConfigureAwait(false);
                    foreach (WalletCommentKeyClientInfo commentKeyInfo in newCommentOutKeys)
                    {
                        AesKeyIvPair commentKey;
                        bool keyAuthenticated;
                        using (var tempPass = _walletCertPass.TempData)
                        {
                            try
                            {
                                commentKey = LightCertificatesHelper.DecryptAesKeyIvPair(
                                    commentKeyInfo.KeyEncryptedFrom,
                                    _settings.WalletCert,
                                    tempPass.Data
                                );
                            }
                            catch (Exception exc)
                            {
                                MiscFuncs.HandleUnexpectedError(exc, _log);
                                continue;
                            }
                        }
                        if (commentKeyInfo.SignatureType == EWalletCommentKeySignatureType.None)
                            keyAuthenticated = false;
                        else
                        {
                            try
                            {
                                Assert.False(commentKeyInfo.AnonymousKey);
                                Assert.NotNull(commentKeyInfo.Signature);
                                Assert.Equal(
                                    commentKeyInfo.Signature.SignerCertificateId,
                                    _settings.WalletCert.Id
                                );
                                Assert.NotNull(commentKeyInfo.Signature.SignatureBytes);
                                keyAuthenticated = _settings.WalletCert.VerifyData(
                                    new SignedData()
                                    {
                                        Data = commentKeyInfo.GetDataForSignature(commentKey.ToBinaryArray()),
                                        Signature = commentKeyInfo.Signature
                                    }
                                );
                            }
                            catch (Exception exc)
                            {
                                MiscFuncs.HandleUnexpectedError(exc, _log);
                                continue;
                            }
                        }
                        _outCommentKeyDb.TryAdd(
                            commentKeyInfo.CommentKeyGuid,
                            MutableTuple.Create(
                                commentKey,
                                keyAuthenticated
                            )
                        );
                    }
                }
                /**/
                var sentMessageForModel = new List<ClientTransferBase>();
                foreach (SentTransactionInfo transactionInfo in outTransferList)
                {
                    byte[] commentBytes;
                    bool authenticatedWalletToCert = _otherWalletCertDict[transactionInfo.WalletTo].Item2;
                    bool authenticatedTransferKey;
                    bool authenticatedTransferDetails;
                    if (transactionInfo.CommentKeyGuid == Guid.Empty)
                    {
                        commentBytes = transactionInfo.Comment;
                        authenticatedTransferKey = false;
                        authenticatedTransferDetails = false;
                    }
                    else
                    {
                        if (!_outCommentKeyDb.ContainsKey(transactionInfo.CommentKeyGuid))
                        {
                            continue;
                        }
                        var commentKey = _outCommentKeyDb[transactionInfo.CommentKeyGuid].Item1;
                        authenticatedTransferKey = _outCommentKeyDb[transactionInfo.CommentKeyGuid].Item2;
                        try
                        {
                            commentBytes = commentKey.DecryptData(transactionInfo.Comment);
                        }
                        catch (Exception exc)
                        {
                            MiscFuncs.HandleUnexpectedError(exc,_log);
                            continue;
                        }
                        if (transactionInfo.SignatureType == ETransferSignatureType.None)
                        {
                            authenticatedTransferDetails = false;
                        }
                        else
                        {
                            if (transactionInfo.EncryptedSignature == null)
                                authenticatedTransferDetails = false;
                            else
                            {
                                var expectedSignature = commentKey.EncryptData(
                                    transactionInfo.InfoForSignature().GetDataToSign(
                                        _settings.WalletCert.Id,
                                        transactionInfo.TransactionSentTime
                                    )
                                );
                                authenticatedTransferDetails = transactionInfo.EncryptedSignature.SequenceEqual(
                                    expectedSignature
                                );
                            }
                        }
                    }
                    var outTransfer = new ClientTransferBase()
                    {
                        Amount = transactionInfo.TransactionAmount,
                        Fee = transactionInfo.OrderFee,
                        WalletTo = transactionInfo.WalletTo,
                        SentTime = transactionInfo.TransactionSentTime,
                        TransferGuid = transactionInfo.TransactionGuid,
                        CommentBytes = commentBytes,
                        TransferNum = Interlocked.Increment(
                            ref _settings.TransferInitCounter
                        ),
                        AnonymousTransfer =
                            transactionInfo.AnonymousTransfer,
                        RequestGuid = transactionInfo.RequestGuid,
                        OutcomeTransfer = true,
                        WalletFrom = _settings.WalletCert.Id,
                        AuthenticatedOtherWalletCert = authenticatedWalletToCert,
                        AuthenticatedCommentKey = authenticatedTransferKey,
                        AuthenticatedTransferDetails = authenticatedTransferDetails
                    };
                    _outcomeTransfersDb.TryAdd(
                        outTransfer.TransferGuid,
                        outTransfer
                    );
                    sentMessageForModel.Add(
                        outTransfer  
                    );
                }
                return new Tuple<Guid, List<ClientTransferBase>>(
                    outTransferList.Any() ? outTransferList.Last().TransactionGuid : Guid.Empty,
                    sentMessageForModel
                );
            }
        }

        private readonly SemaphoreSlim _processSentTranfersLockSem
            = new SemaphoreSlim(1);
        private async void ProcessSentTranfers()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _processSentTranfersLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _processSentTranfersLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var nextSentChunk = await GetOutcomeTransfersChunk(
                                _settings.LastKnownSentTransferSentTime,
                                DateTime.MaxValue,
                                _settings.LastKnownSentTransferGuid,
                                CancellationToken.None
                            ).ConfigureAwait(false);
                            if(nextSentChunk.Item2.Count == 0)
                                return;
                            if(nextSentChunk.Item1 != Guid.Empty)
                                _settings.LastKnownSentTransferGuid
                                    = nextSentChunk.Item1;
                            if (nextSentChunk.Item2.Count > 0)
                            {
                                _walletSessionModel.OnTransferSent.OnNext(
                                    nextSentChunk.Item2
                                );
                                lockSemCalledWrapper.Called = true;
                            }
                            if (
                                nextSentChunk.Item2.Count
                                    < _settings.BufferCount
                            )
                            {
                                lockSemCalledWrapper.Called = false;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                MiscFuncs.HandleUnexpectedError(exc,_log);
            }
        }

        private async Task<WalletCommentKeyClientInfo> RegisterNewOutKey(
            Guid walletTo, bool anonymousKey)
        {
            var commentKeyAesInfo = AesKeyIvPair.GenAesKeyIvPair();
            await UpdateOtherWalletCertInfoPrivate(new[] {walletTo}).ConfigureAwait(false);
            var walletToPublicCert = _otherWalletCertDict[walletTo].Item1;
            Assert.NotNull(walletToPublicCert);
            var aesKeyIvPairEncryptedFromWalletCert
                = LightCertificatesHelper.EncryptAesKeyIvPair(
                    commentKeyAesInfo,
                    _settings.WalletCert
                );
            var aesKeyIvPairEncryptedToWalletCert =
                LightCertificatesHelper.EncryptAesKeyIvPair(
                    commentKeyAesInfo,
                    walletToPublicCert
                );
            var requestGuid = Guid.NewGuid();
            var commentKeyGuid = MiscFuncs.GenRandMaskedGuid(
                _serverInfo.TransferAndCommentKeyGuidMask,
                _serverInfo.TransferAndCommentKeyGuidMaskEqual
            );
            var genNewKeyRequest = new GenNewCommentKeyOrderRequest()
            {
                RequestGuid = requestGuid,
                KeyInfo = new WalletCommentKeyClientInfo()
                {
                    AnonymousKey = anonymousKey,
                    WalletFrom = _settings.WalletCert.Id,
                    WalletTo = walletTo,
                    KeyAlg = EWalletCommentKeyAlg.Aes256,
                    CommentKeyGuid = commentKeyGuid,
                    KeyEncryptedFrom = aesKeyIvPairEncryptedFromWalletCert,
                    KeyEncryptedTo = aesKeyIvPairEncryptedToWalletCert,
                    SignatureType = EWalletCommentKeySignatureType.None,
                    Signature = null
                }
            };
            while (true)
            {
                genNewKeyRequest.KeyInfo.IssuedTime 
                    = MiscFuncs.RoundDateTimeToSeconds(
                        await _getRelativeTimeInterface.GetNowTime().ConfigureAwait(false)
                    );
                genNewKeyRequest.KeyInfo.ValidUntilTime
                    = MiscFuncs.RoundDateTimeToSeconds(
                        genNewKeyRequest.KeyInfo.IssuedTime + _settings.CommentKeyLifetime
                    );
                if (!anonymousKey)
                {
                    genNewKeyRequest.KeyInfo.SignatureType = EWalletCommentKeySignatureType.Type20150929;
                    var signData = genNewKeyRequest.KeyInfo.GetDataForSignature(
                        commentKeyAesInfo.ToBinaryArray()
                    );
                    using (var tempData = _walletCertPass.TempData)
                    {
                        genNewKeyRequest.KeyInfo.Signature = _settings.WalletCert.SignData(
                            signData,
                            tempData.Data
                            ).Signature;
                    }
                }
                try
                {
                    await _authedWalletInterface.GenerateNewCommentKey(
                        genNewKeyRequest
                        ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                return genNewKeyRequest.KeyInfo;
            }
        }
    }
}
