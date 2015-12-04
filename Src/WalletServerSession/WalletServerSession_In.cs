using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.GeneralClientInterfaces.WalletServer;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.BitMoneyClient.Lib.WalletServerSession
{
    public partial class WalletServerSession
    {
        // . Authenticated
        private readonly Dictionary<Guid, MutableTuple<AesKeyIvPair,bool>> _inCommentKeyDb
            = new Dictionary<Guid, MutableTuple<AesKeyIvPair,bool>>();
        private readonly ConcurrentDictionary<Guid, ClientTransferBase> _incomeTransfersDb
            = new ConcurrentDictionary<Guid, ClientTransferBase>();
        
        /// <returns>Last transaction GUID, chunk of transactions</returns>
        public async Task<Tuple<Guid, List<ClientTransferBase>>>
            GetIncomeTransfersChunk(
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
                var receivedTransactionList = await MiscFuncs.RepeatWhileTimeout(
                    async () =>
                    {
                        var request = new SubscribeIncomingTransactionsOrderRequest()
                        {
                            BufferCount = _settings.BufferCount,
                            LastKnownTransactionGuid =
                                lastKnownReceivedTransferGuid,
                            SentTimeFrom = fromTime,
                            SentTimeTo = toTime,
                            IncludeTransferGuidOnlyList = includeTransferGuidOnlyList
                        };
                        request.CheckMe();
                        return await _authedWalletInterface.SubscribeIncomeTransactions(
                            request
                        ).ConfigureAwait(false);
                    },
                    _cts.Token,
                    token
                ).ConfigureAwait(false);
                /**/
                var userCertGuidToUpdate =
                    receivedTransactionList.Select(_ => _.WalletFrom)
                        .Except(new[]
                        {WalletServerClientConstants.AnonymousWalletGuid, WalletServerClientConstants.FeesWalletGuid})
                        .Distinct()
                        .ToList();
                await UpdateOtherWalletCertInfoPrivate(userCertGuidToUpdate).ConfigureAwait(false);
                /**/
                var commentKeysToDownload = receivedTransactionList
                    .Select(x => x.CommentKeyId)
                    .Where(x =>
                        x != Guid.Empty
                        && !_inCommentKeyDb.ContainsKey(x)
                    ).Distinct().ToList();
                if (commentKeysToDownload.Any())
                {
                    var newCommentInKeys = await MiscFuncs.RepeatWhileTimeout(
                        async () => await _authedWalletInterface.GetCommentInKeyList(
                            commentKeysToDownload
                        ).ConfigureAwait(false),
                        _cts.Token,
                        token
                    ).ConfigureAwait(false);
                    foreach (WalletCommentKeyClientInfo keyInfo in newCommentInKeys)
                    {
                        AesKeyIvPair messageKey;
                        using (var tempPass = _walletCertPass.TempData)
                        {
                            try
                            {
                                messageKey =
                                    LightCertificatesHelper.DecryptAesKeyIvPair(
                                        keyInfo.KeyEncryptedTo,
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
                        bool authenticatedKey;
                        if (
                            keyInfo.AnonymousKey
                            || keyInfo.SignatureType == EWalletCommentKeySignatureType.None
                            || keyInfo.WalletFrom.In(
                                WalletServerClientConstants.FeesWalletGuid,
                                WalletServerClientConstants.AnonymousWalletGuid
                            )
                        )
                        {
                            authenticatedKey = false;
                        }
                        else
                        {
                            try
                            {
                                var walletFromCert = _otherWalletCertDict[keyInfo.WalletFrom].Item1;
                                authenticatedKey = walletFromCert.VerifyData(
                                    new SignedData()
                                    {
                                        Signature = keyInfo.Signature,
                                        Data = keyInfo.GetDataForSignature(messageKey.ToBinaryArray())
                                    }
                                );
                            }
                            catch (Exception exc)
                            {
                                MiscFuncs.HandleUnexpectedError(exc, _log);
                                continue;
                            }
                        }
                        _inCommentKeyDb.Add(
                            keyInfo.CommentKeyGuid, 
                            MutableTuple.Create(
                                messageKey,
                                authenticatedKey
                            )
                        );
                    }
                }
                /**/
                var receivedTransfers = new List<ClientTransferBase>();
                foreach (
                    IncomingTransactionInfo transactionInfo
                        in receivedTransactionList
                )
                {
                    var unknownWalletFrom = transactionInfo.AnonymousTransfer || transactionInfo.WalletFrom.In(
                        WalletServerClientConstants.FeesWalletGuid,
                        WalletServerClientConstants.AnonymousWalletGuid
                    );
                    var authenticatedWalletFromCert = !unknownWalletFrom 
                        && _otherWalletCertDict[transactionInfo.WalletFrom].Item2;
                    bool authenticatedKey;
                    bool authenticatedTransferDetails;
                    var commentKeyId = transactionInfo.CommentKeyId;
                    byte[] commentBytes;
                    if (commentKeyId == Guid.Empty)
                    {
                        commentBytes = transactionInfo.Comment;
                        authenticatedKey = false;
                        authenticatedTransferDetails = false;
                    }
                    else
                    {
                        if(!_inCommentKeyDb.ContainsKey(commentKeyId))
                            continue;
                        var commentKey = _inCommentKeyDb[commentKeyId].Item1;
                        authenticatedKey = _inCommentKeyDb[commentKeyId].Item2;
                        try
                        {
                            commentBytes = commentKey.DecryptData(
                                transactionInfo.Comment
                            );
                        }
                        catch (Exception exc)
                        {
                            MiscFuncs.HandleUnexpectedError(exc,_log);
                            continue;
                        }
                        if (
                            transactionInfo.SignatureType == ETransferSignatureType.None
                            || unknownWalletFrom
                        )
                        {
                            authenticatedTransferDetails = false;
                        }
                        else
                        {
                            try
                            {
                                authenticatedTransferDetails = transactionInfo.EncryptedSignature.SequenceEqual(
                                    commentKey.EncryptData(
                                        transactionInfo.ForSignatureInfo(_settings.WalletCert.Id).GetDataToSign(
                                            transactionInfo.WalletFrom,
                                            transactionInfo.TransactionSentTime
                                        )
                                    )
                                );
                            }
                            catch (Exception exc)
                            {
                                MiscFuncs.HandleUnexpectedError(exc, _log);
                                authenticatedTransferDetails = false;
                            }
                        }
                    }
                    var incomeTransfer = new ClientTransferBase()
                    {
                        Amount = transactionInfo.TransactionAmount,
                        CommentBytes = commentBytes,
                        SentTime = transactionInfo.TransactionSentTime,
                        TransferGuid = transactionInfo.TransactionGuid,
                        WalletFrom = transactionInfo.WalletFrom,
                        TransferNum = Interlocked.Increment(
                            ref _settings.TransferInitCounter
                            ),
                        AnonymousTransfer = transactionInfo.AnonymousTransfer,
                        OutcomeTransfer = false,
                        RequestGuid = Guid.Empty,
                        WalletTo = _settings.WalletCert.Id,
                        AuthenticatedOtherWalletCert = authenticatedWalletFromCert,
                        AuthenticatedCommentKey = authenticatedKey,
                        AuthenticatedTransferDetails = authenticatedTransferDetails
                    };
                    _incomeTransfersDb.TryAdd(
                        incomeTransfer.TransferGuid,
                        incomeTransfer
                    );
                    receivedTransfers.Add(
                        incomeTransfer
                    );
                }
                return new Tuple<Guid, List<ClientTransferBase>>(
                    receivedTransactionList.Any()
                        ? receivedTransactionList.Last().TransactionGuid
                        : Guid.Empty,
                    receivedTransfers
                );
            }
        }
        private readonly SemaphoreSlim _processIncomeTransfersLockSem = new SemaphoreSlim(1);
        private async void ProcessIncomeTransfers()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _processIncomeTransfersLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _processIncomeTransfersLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var receivedTransfersForModel = await GetIncomeTransfersChunk(
                                _settings.LastKnownReceivedTransferSentTime,
                                DateTime.MaxValue,
                                _settings.LastKnownReceivedTransferGuid,
                                CancellationToken.None
                                ).ConfigureAwait(false);
                            if (receivedTransfersForModel.Item1 != Guid.Empty)
                                _settings.LastKnownReceivedTransferGuid
                                    = receivedTransfersForModel.Item1;
                            if (receivedTransfersForModel.Item2.Count > 0)
                            {
                                _walletSessionModel.OnTransferReceived.OnNext(
                                    receivedTransfersForModel.Item2
                                );
                                lockSemCalledWrapper.Called = true;
                            }
                        }
                    }
                }
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (OperationCanceledException)
            {
                }
            catch (Exception exc)
            {
                MiscFuncs.HandleUnexpectedError(exc,_log);
            }
        }
    }
}
