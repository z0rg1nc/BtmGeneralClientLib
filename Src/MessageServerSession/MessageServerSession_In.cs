using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using BtmI2p.AesHelper;
using BtmI2p.GeneralClientInterfaces.MessageServer;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using MoreLinq;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.MessageServerSession
{
    public partial class MessageServerSession
    {
        private readonly SemaphoreSlim _processIncomeMessagesLockSem = new SemaphoreSlim(1);
        private async void ProcessIncomeMessages()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _processIncomeMessagesLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _processIncomeMessagesLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var receivedMessageList = await MiscFuncs.RepeatWhileTimeout(
                                async () => await _authedMessageInterface.GetIncomingMessages(
                                    new GetIncomeMessageCommandRequest()
                                    {
                                        LastKnownMessageGuid = _settings.LastKnownReceivedMessageGuid,
                                        UserFrom = Guid.Empty,
                                        BufferCount = _settings.BufferCount,
                                        MessageTypeFilter = (int) EUserMessageType.Utf8Text,
                                        SentTimeFrom =
                                            await _getTimeInterface.GetNowTime().ConfigureAwait(false)
                                            - _settings.InitLoadTimeFrame
                                    }
                                    ).ConfigureAwait(false),
                                _cts.Token
                                ).ConfigureAwait(false);
                            if (receivedMessageList.Count == 0)
                                return;
                            lockSemCalledWrapper.Called = true;
                            _settings.LastKnownReceivedMessageGuid =
                                receivedMessageList.Last().MessageGuid;
                            /**/
                            await UpdateOtherUserCertInfo(
                                receivedMessageList.Select(_ => _.UserFromGuid).Distinct().ToList()
                                ).ConfigureAwait(false);
                            /**/
                            var messageKeysToDownload = receivedMessageList
                                .Select(x => x.MessageKeyGuid)
                                .Where(x =>
                                    x != Guid.Empty
                                    && !_inMessageKeyDb.ContainsKey(x)
                                ).Distinct().ToList();
                            var newMessageInKeys = new List<MessageKeyClientInfo>();
                            if (messageKeysToDownload.Any())
                            {
                                foreach (var keysChunk in messageKeysToDownload.Batch(20).Select(_ => _.ToList()))
                                {
                                    newMessageInKeys.AddRange(
                                        await MiscFuncs.RepeatWhileTimeout(
                                            async () => await _authedMessageInterface.GetMessageInKeyList(
                                                keysChunk
                                                ).ConfigureAwait(false)
                                            ).ConfigureAwait(false)
                                        );
                                }
                                foreach (MessageKeyClientInfo keyInfo in newMessageInKeys)
                                {
                                    AesKeyIvPair messageKey;
                                    var userFromCert = _otherUserCertificateDict[keyInfo.UserFromGuid].Item1;
                                    Assert.NotNull(userFromCert);
                                    bool authenticatedKey;
                                    using (var tempPass = _userCertPass.TempData)
                                    {
                                        authenticatedKey = keyInfo.CheckInKeySignature(
                                            userFromCert,
                                            _settings.UserCert,
                                            tempPass.Data
                                            );
                                        try
                                        {
                                            messageKey =
                                                LightCertificatesHelper.DecryptAesKeyIvPair(
                                                    keyInfo.KeyEncryptedTo,
                                                    _settings.UserCert,
                                                    tempPass.Data
                                                    );
                                        }
                                        catch (Exception exc)
                                        {
                                            MiscFuncs.HandleUnexpectedError(exc, _log);
                                            continue;
                                        }
                                    }
                                    _inMessageKeyDb.Add(
                                        keyInfo.MessageKeyGuid,
                                        MutableTuple.Create(
                                            messageKey,
                                            authenticatedKey
                                            )
                                        );
                                }
                            }
                            /**/
                            var receivedMessageForModel = new List<ClientTextMessage>();
                            foreach (IncomingMessageInfo info in receivedMessageList)
                            {
                                var messageKeyId = info.MessageKeyGuid;
                                string messageText = null;
                                bool authenticatedKey;
                                bool authenticatedMessage;
                                if (messageKeyId == Guid.Empty)
                                {
                                    try
                                    {
                                        messageText = Encoding.UTF8.GetString(info.MessageBody);
                                    }
                                    catch (Exception exc)
                                    {
                                        MiscFuncs.HandleUnexpectedError(exc, _log);
                                        continue;
                                    }
                                    authenticatedKey = false;
                                    authenticatedMessage = false;
                                }
                                else
                                {
                                    AesKeyIvPair messageKey;
                                    if (_inMessageKeyDb.ContainsKey(messageKeyId))
                                    {
                                        messageKey = _inMessageKeyDb[messageKeyId].Item1;
                                        authenticatedKey = _inMessageKeyDb[messageKeyId].Item2;
                                    }
                                    else
                                    {
                                        continue;
                                    }
                                    try
                                    {
                                        byte[] decryptedMessageData =
                                            messageKey.DecryptData(info.MessageBody);
                                        messageText = Encoding.UTF8.GetString(
                                            decryptedMessageData
                                            );
                                    }
                                    catch (Exception exc)
                                    {
                                        MiscFuncs.HandleUnexpectedError(exc, _log);
                                        continue;
                                    }
                                    authenticatedMessage = info.CheckSignature(
                                        _settings.UserCert.Id,
                                        messageKey
                                        );
                                }
                                var authenticatedUserFrom = _otherUserCertificateDict[info.UserFromGuid].Item2;
                                receivedMessageForModel.Add(
                                    new ClientTextMessage()
                                    {
                                        UserFrom = info.UserFromGuid,
                                        SentTime = info.MessageSentTime,
                                        SaveUntil = info.MessageSaveUntil,
                                        MessageText = messageText,
                                        MessageGuid = info.MessageGuid,
                                        MessageNum =
                                            Interlocked.Increment(
                                                ref _settings.MessagesInitCounter
                                                ),
                                        OutcomeMessage = false,
                                        UserTo = _settings.UserCert.Id,
                                        MessageKeyAuthenticated = authenticatedKey,
                                        MessageAuthenticated = authenticatedMessage,
                                        OtherUserCertAuthenticated = authenticatedUserFrom
                                    }
                                );
                            }
                            if (receivedMessageForModel.Count > 0)
                            {
                                _sessionModel.IncomeMessageReceived.OnNext(
                                    receivedMessageForModel
                                );
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
        // pair, auhenticated
        private readonly Dictionary<Guid, MutableTuple<AesKeyIvPair,bool>> _inMessageKeyDb
            = new Dictionary<Guid, MutableTuple<AesKeyIvPair, bool>>();
    }
}
