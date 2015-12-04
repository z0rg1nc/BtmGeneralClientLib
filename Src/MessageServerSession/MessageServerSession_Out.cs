using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.AesHelper;
using BtmI2p.GeneralClientInterfaces.MessageServer;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.LightCertificates.Lib;
using BtmI2p.MiscUtils;
using BtmI2p.MyNotifyPropertyChanged;
using BtmI2p.ObjectStateLib;
using MoreLinq;
using Xunit;

namespace BtmI2p.BitMoneyClient.Lib.MessageServerSession
{
    public partial class MessageServerSession
    {
        private readonly SemaphoreSlim _processSentMessagesLockSem
            = new SemaphoreSlim(1);
        private async void ProcessSentMessages()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _processSentMessagesLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _processSentMessagesLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            var outMessageList = await MiscFuncs.RepeatWhileTimeout(
                                async () =>  await _authedMessageInterface.GetSentMessages(
                                    new GetSentMessagesCommandRequest()
                                    {
                                        DateTimeFrom =
                                            await _getTimeInterface.GetNowTime().ConfigureAwait(false)
                                                - _settings.InitLoadTimeFrame,
                                        DateTimeTo = DateTime.MaxValue,
                                        UserTo = Guid.Empty,
                                        LastKnownMessageGuid =
                                            _settings.LastKnownSentMessageGuid,
                                        BufferCount = _settings.BufferCount,
                                        MessageTypeFilter = (int) EUserMessageType.Utf8Text
                                    }
                                ).ConfigureAwait(false),
                                _cts.Token
                            ).ConfigureAwait(false);
                            if (outMessageList.Count == 0)
                                return;
                            lockSemCalledWrapper.Called = true;
                            _settings.LastKnownSentMessageGuid =
                                outMessageList.Last().MessageGuid;
                            /**/
                            var userToCertsToDownload = outMessageList.Select(_ => _.UserToGuid).ToList();
                            await UpdateOtherUserCertInfo(userToCertsToDownload).ConfigureAwait(false);
                            /**/
                            var messageKeysToDownload = outMessageList
                                .Select(x => x.MessageKeyGuid)
                                .Where(
                                    x => 
                                        x != Guid.Empty 
                                        && !_outMessageKeyDb.ContainsKey(x)
                                ).Distinct().ToList();
                            if (messageKeysToDownload.Any())
                            {
                                var newMessageOutKeys = new List<MessageKeyClientInfo>();
                                foreach (var keysChunk in messageKeysToDownload.Batch(20).Select(_ => _.ToList()))
                                {
                                    newMessageOutKeys.AddRange(
                                        await MiscFuncs.RepeatWhileTimeout(
                                            async () => await _authedMessageInterface.GetMessageOutKeyList(
                                                keysChunk
                                                ).ConfigureAwait(false),
                                            _cts.Token
                                        ).ConfigureAwait(false)
                                    );
                                }
                                foreach (MessageKeyClientInfo messageKeyInfo in newMessageOutKeys)
                                {
                                    Assert.NotNull(messageKeyInfo);
                                    AesKeyIvPair messageKey;
                                    bool authenticatedKey;
                                    using (var tempPass = _userCertPass.TempData)
                                    {
                                        authenticatedKey = messageKeyInfo.CheckOutKeySignature(
                                            _settings.UserCert,
                                            tempPass.Data
                                        );
                                        try
                                        {
                                            messageKey =
                                                LightCertificatesHelper.DecryptAesKeyIvPair(
                                                    messageKeyInfo
                                                        .KeyEncryptedFrom,
                                                    _settings.UserCert,
                                                    tempPass.Data
                                                );
                                        }
                                        catch (Exception exc)
                                        {
                                            MiscFuncs.HandleUnexpectedError(exc,_log);
                                            continue;
                                        }
                                    }
                                    _outMessageKeyDb.TryAdd(
                                        messageKeyInfo.MessageKeyGuid,
                                        MutableTuple.Create(messageKey,authenticatedKey)
                                    );
                                }
                            }
                            /**/
                            var sentMessagesForModel = new List<ClientTextMessage>();
                            foreach (SentMessageInfo messageInfo in outMessageList)
                            {
                                string messageText = null;
                                bool authenticatedUserToCert = _otherUserCertificateDict[messageInfo.UserToGuid].Item2;
                                bool authenticatedKey;
                                bool authenticatedMessage;
                                if (messageInfo.MessageKeyGuid == Guid.Empty)
                                {
                                    authenticatedKey = false;
                                    authenticatedMessage = false;
                                    try
                                    {
                                        messageText = Encoding.UTF8.GetString(
                                            messageInfo.MessageBody
                                        );
                                    }
                                    catch (Exception exc)
                                    {
                                        MiscFuncs.HandleUnexpectedError(exc,_log);
                                        continue;
                                    }
                                }
                                else
                                {
                                    AesKeyIvPair messageKey;
                                    if (_outMessageKeyDb.ContainsKey(messageInfo.MessageKeyGuid))
                                    {
                                        messageKey = _outMessageKeyDb[messageInfo.MessageKeyGuid].Item1;
                                        authenticatedKey = _outMessageKeyDb[messageInfo.MessageKeyGuid].Item2;
                                    }
                                    else
                                    {
                                        continue;
                                    }
                                    try
                                    {
                                        messageText = Encoding.UTF8.GetString(
                                            messageKey.DecryptData(messageInfo.MessageBody)
                                        );
                                    }
                                    catch (Exception exc)
                                    {
                                        MiscFuncs.HandleUnexpectedError(exc,_log);
                                        continue;
                                    }
                                    authenticatedMessage = messageInfo.CheckSignature(
                                        _settings.UserCert.Id,
                                        messageKey
                                    );
                                }
                                sentMessagesForModel.Add(
                                    new ClientTextMessage()
                                    {
                                        MessageGuid = messageInfo.MessageGuid,
                                        UserTo = messageInfo.UserToGuid,
                                        MessageText = messageText,
                                        SentTime = messageInfo.MessageSentTime,
                                        SaveUntil = messageInfo.MessageSaveUntil,
                                        MessageNum = Interlocked.Increment(
                                            ref _settings.MessagesInitCounter
                                        ),
                                        OutcomeMessage = true,
                                        UserFrom = _settings.UserCert.Id,
                                        OtherUserCertAuthenticated = authenticatedUserToCert,
                                        MessageKeyAuthenticated = authenticatedKey,
                                        MessageAuthenticated = authenticatedMessage
                                    }
                                );
                            }
                            if (sentMessagesForModel.Count > 0)
                            {
                                _sessionModel.OutcomeMessageSent
                                    .OnNext(sentMessagesForModel);
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
                MiscFuncs.HandleUnexpectedError(exc, _log);
            }
        }

        private readonly SemaphoreSlim _offlineMessagesLockSem
            = new SemaphoreSlim(1);
        // . Authenticated
        private readonly ConcurrentDictionary<Guid, MutableTuple<AesKeyIvPair,bool>> _outMessageKeyDb
            = new ConcurrentDictionary<Guid, MutableTuple<AesKeyIvPair, bool>>();
        // . Authenticated
        private readonly Dictionary<Guid, MutableTuple<MessageKeyClientInfo,bool>> _cachedActiveOutMessageKeyInfoDb
            = new Dictionary<Guid, MutableTuple<MessageKeyClientInfo, bool>>();
        private async Task<MessageKeyClientInfo> RegisterNewOutKey(Guid userTo)
        {
            var messageKeyAesInfo = AesKeyIvPair.GenAesKeyIvPair();
            if (!_otherUserCertificateDict.ContainsKey(userTo))
            {
                await UpdateOtherUserCertInfo(new[] {userTo}).ConfigureAwait(false);
            }
            var userToPublicCert = _otherUserCertificateDict[userTo].Item1;
            var keyIvPairEncryptedFrom 
                = LightCertificatesHelper.EncryptAesKeyIvPair(
                    messageKeyAesInfo,
                    _settings.UserCert
                );
            var keyIvPairEncryptedTo
                = LightCertificatesHelper.EncryptAesKeyIvPair(
                    messageKeyAesInfo,
                    userToPublicCert
                );
            var requestGuid = MiscFuncs.GenGuidWithFirstBytes(0);
            var newKeyGuid = MiscFuncs.GenRandMaskedGuid(
                _serverInfoForClient.MessageAndMessageKeyGuidMask,
                _serverInfoForClient.MessageAndMessageKeyGuidMaskEqual
            );
            while (true)
            {
                var nowTime =
                    MiscFuncs.RoundDateTimeToSeconds(await _getTimeInterface.GetNowTime().ConfigureAwait(false));
                var genNewKeyRequest = new GenNewMessageKeyCommandRequest()
                {
                    RequestGuid = requestGuid,
                    KeyInfo = new MessageKeyClientInfo()
                    {
                        IssuedTime = nowTime,
                        ValidUntil = MiscFuncs.RoundDateTimeToSeconds(
                            nowTime
                            + _settings.MessageKeyLifetime
                        ),
                        KeyAlgType = EKeyAlgType.Aes256,
                        UserToGuid = userTo,
                        UserFromGuid = _settings.UserCert.Id,
                        KeySignatureType = EMessageKeySignature.Type20150923,
                        MessageKeyGuid = newKeyGuid,
                        KeyEncryptedFrom = keyIvPairEncryptedFrom,
                        KeyEncryptedTo = keyIvPairEncryptedTo
                    }
                };
                using (var tempData = _userCertPass.TempData)
                {
                    genNewKeyRequest.KeyInfo.KeySignature = _settings.UserCert.SignData(
                        genNewKeyRequest.KeyInfo.GetByteArrayForSigning(messageKeyAesInfo.ToBinaryArray()),
                        tempData.Data
                    ).Signature;
                }
                try
                {
                    await _authedMessageInterface
                        .SaveNewMessageKey(
                            genNewKeyRequest
                        ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                    return genNewKeyRequest.KeyInfo;
                }
                catch (TimeoutException)
                {
                }
            }
        }
        private readonly SemaphoreSlim _processOfflineMessagesQueueLockSem
            = new SemaphoreSlim(1);
        private async void ProcessOfflineMessagesQueue()
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var lockSemCalledWrapper = _processOfflineMessagesQueueLockSem.GetCalledWrapper();
                    lockSemCalledWrapper.Called = true;
                    using (await _processOfflineMessagesQueueLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
                        while (!_cts.IsCancellationRequested && lockSemCalledWrapper.Called)
                        {
                            lockSemCalledWrapper.Called = false;
                            PreparedToSendMessage nextMessage;
                            IList<Guid> userCertsToUpdate;
                            using (await _offlineMessagesLockSem.GetDisposable().ConfigureAwait(false))
                            {
                                if (_sessionModel.OfflineMessages.Count == 0)
                                    return;
                                lockSemCalledWrapper.Called = true;
                                nextMessage = _sessionModel.OfflineMessages.Peek();
                                userCertsToUpdate = _sessionModel.OfflineMessages.Select(_ => _.UserTo).ToList();
                            }
                            await UpdateOtherUserCertInfo(userCertsToUpdate).ConfigureAwait(false);
                            byte[] origMessageBody = Encoding.UTF8.GetBytes(
                                nextMessage.MessageText
                            );
                            byte[] messageBody;
                            Guid messageKeyGuid;
                            AesKeyIvPair messageAesKey = null;
                            bool userToCertAuthenticated = _otherUserCertificateDict[nextMessage.UserTo].Item2;
                            bool outKeyAuthenticated;
                            if (!nextMessage.EncryptMessage)
                            {
                                messageKeyGuid = Guid.Empty;
                                messageBody = origMessageBody;
                                outKeyAuthenticated = false;
                            }
                            else
                            {
                                MessageKeyClientInfo nextKeyClientMessageInfo;
                                if (
                                    !_cachedActiveOutMessageKeyInfoDb.ContainsKey(
                                        nextMessage.UserTo
                                    )
                                )
                                {
                                    var outKeyList = await MiscFuncs.RepeatWhileTimeout(
                                        async () => await _authedMessageInterface.GetValidMessageOutKeyList(
                                                    nextMessage.UserTo
                                                ).ConfigureAwait(false),
                                        _cts.Token
                                    ).ConfigureAwait(false);
                                    if (outKeyList.Count == 0)
                                    {
                                        nextKeyClientMessageInfo = await RegisterNewOutKey(
                                            nextMessage.UserTo
                                        ).ConfigureAwait(false);
                                        outKeyAuthenticated = true;
                                    }
                                    else
                                    {
                                        nextKeyClientMessageInfo = outKeyList
                                            .OrderByDescending(x => x.ValidUntil)
                                            .First();
                                        using (var tempData = _userCertPass.TempData)
                                        {
                                            outKeyAuthenticated = nextKeyClientMessageInfo.CheckOutKeySignature(
                                                _settings.UserCert,
                                                tempData.Data
                                            );
                                        }
                                    }
                                    _cachedActiveOutMessageKeyInfoDb.Add(
                                        nextMessage.UserTo,
                                        MutableTuple.Create(
                                            nextKeyClientMessageInfo,
                                            outKeyAuthenticated
                                        )
                                    );
                                }
                                else
                                {
                                    nextKeyClientMessageInfo = _cachedActiveOutMessageKeyInfoDb[nextMessage.UserTo].Item1;
                                    outKeyAuthenticated = _cachedActiveOutMessageKeyInfoDb[nextMessage.UserTo].Item2;
                                    if (
                                        nextKeyClientMessageInfo.ValidUntil
                                        <= await _getTimeInterface.GetNowTime().ConfigureAwait(false) + TimeSpan.FromMinutes(1)
                                    )
                                    {
                                        _cachedActiveOutMessageKeyInfoDb.Remove(nextMessage.UserTo);
                                        continue;
                                    }
                                }
                                
                                if (_outMessageKeyDb.ContainsKey(nextKeyClientMessageInfo.MessageKeyGuid))
                                    messageAesKey = _outMessageKeyDb[nextKeyClientMessageInfo.MessageKeyGuid].Item1;
                                else
                                {
                                    using (var tempPass = _userCertPass.TempData)
                                    {
                                        messageAesKey = LightCertificatesHelper.DecryptAesKeyIvPair(
                                            nextKeyClientMessageInfo.KeyEncryptedFrom,
                                            _settings.UserCert,
                                            tempPass.Data
                                        );
                                    }
                                    _outMessageKeyDb.TryAdd(
                                        nextKeyClientMessageInfo.MessageKeyGuid,
                                        MutableTuple.Create(
                                            messageAesKey,
                                            outKeyAuthenticated
                                        )
                                    );
                                }
                                messageKeyGuid = nextKeyClientMessageInfo.MessageKeyGuid;
                                messageBody = messageAesKey.EncryptData(origMessageBody);
                            }
                            SendMessageCommandResponse sendMessageCommandResponse = null;
                            SendMessageCommandRequest sendMessageRequest = null;
                            var messageGuid = MiscFuncs.GenRandMaskedGuid(
                                _serverInfoForClient.MessageAndMessageKeyGuidMask,
                                _serverInfoForClient.MessageAndMessageKeyGuidMaskEqual
                            );
                            while (!_cts.IsCancellationRequested)
                            {
                                sendMessageRequest = new SendMessageCommandRequest(
                                    await _getTimeInterface.GetNowTime().ConfigureAwait(false),
                                    nextMessage.KeepForTs
                                )
                                {
                                    MessageType = (int) EUserMessageType.Utf8Text,
                                    MessageBody = messageBody,
                                    UserTo = nextMessage.UserTo,
                                    MessageKeyGuid = messageKeyGuid,
                                    MaxMessageFee = nextMessage.MaxMessageFee,
                                    RequestGuid = nextMessage.RequestGuid,
                                    MessageGuid = messageGuid
                                };
                                if (messageKeyGuid == Guid.Empty)
                                {
                                    sendMessageRequest.SignatureType = EMessageSignatureType.None;
                                    sendMessageRequest.EncryptedSignature = new byte[0];
                                }
                                else
                                {
                                    sendMessageRequest.SignatureType = EMessageSignatureType.Type20150924;
                                    Assert.NotNull(messageAesKey);
                                    sendMessageRequest.EncryptedSignature = messageAesKey.EncryptData(
                                        sendMessageRequest.GetDataForSignature(
                                            _settings.UserCert.Id
                                        )
                                    );
                                }
                                try
                                {
                                    sendMessageCommandResponse = await _authedMessageInterface.SendMessage(
                                        sendMessageRequest
                                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                    decimal actualMessageFee = sendMessageCommandResponse.MessageFee;
                                    if(
                                        Math.Abs(
                                            actualMessageFee 
                                            - (nextMessage.MaxMessageFee - MaxFeeSurplus)
                                        ) > 0.01m
                                    )
                                        _sessionModel.NeedToUpdateContactInfos.OnNext(null);
                                }
                                catch (RpcRethrowableException rpcExc)
                                {
                                    _log.Error(
                                        "_authedUserInterface.SendMessage rpc error {0}",
                                        rpcExc.ToString()
                                    );
                                    _sessionModel.SendingMessageError.OnNext(
                                        new Tuple<PreparedToSendMessage, string>(
                                            nextMessage,
                                            rpcExc.Message
                                        )
                                    );
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (TimeoutException)
                                {
                                    continue;
                                }
                                catch (Exception exc)
                                {
                                    _log.Error(
                                        "_authedUserInterface.SendMessage error {0}",
                                        exc.ToString()
                                    );
                                    _sessionModel.SendingMessageError.OnNext(
                                        new Tuple<PreparedToSendMessage, string>(
                                            nextMessage,
                                            exc.Message
                                        )
                                    );
                                }
                                break;
                            }
                            using (await _offlineMessagesLockSem.GetDisposable().ConfigureAwait(false))
                            {
                                _sessionModel.OfflineMessages.Dequeue();
                                MyNotifyPropertyChangedArgs.RaiseProperyChanged(
                                    _sessionModel,
                                    e => e.OfflineMessages
                                );
                            }
                            if (sendMessageCommandResponse != null)
                            {
                                /*
                                _sessionModel.OutcomeMessageSent.OnNext(
                                    new List<ClientTextMessage>()
                                    {
                                        new ClientTextMessage()
                                        {
                                            MessageText = nextMessage.MessageText,
                                            SentTime = sendMessageRequest.SentDateTime,
                                            SaveUntil = sendMessageRequest.SaveUntil,
                                            UserTo = nextMessage.UserTo,
                                            MessageGuid = sendMessageCommandResponse.MessageGuid,
                                            MessageNum =
                                                Interlocked.Increment(
                                                    ref _settings.MessagesInitCounter
                                                ),
                                            OutcomeMessage = true,
                                            UserFrom = _settings.UserCert.Id,
                                            OtherUserCertAuthenticated = userToCertAuthenticated,
                                            MessageKeyAuthenticated = outKeyAuthenticated,
                                            MessageAuthenticated = true
                                        }
                                    }
                                );
                                */
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
            finally
            {
                UpdateBalance();
            }
        }

        public async Task SendTextMessage(
            string message,
            Guid userTo,
            TimeSpan keepForTs,
            bool encryptMessage = true,
            decimal maxMessageFee = 0.0m
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                using (await _offlineMessagesLockSem.GetDisposable().ConfigureAwait(false))
                {
                    if (_sessionModel.Balance < maxMessageFee)
                    {
                        throw new Exception("Not enough funds");
                    }
                    _sessionModel.OfflineMessages.Enqueue(
                        new PreparedToSendMessage()
                        {
                            MessageText = message,
                            UserTo = userTo,
                            EncryptMessage = encryptMessage,
                            KeepForTs = keepForTs,
                            MaxMessageFee = maxMessageFee
                        }
                    );
                    MyNotifyPropertyChangedArgs.RaiseProperyChanged(
                        _sessionModel,
                        e => e.OfflineMessages
                    );
                }
            }
            ProcessOfflineMessagesQueue();
        }
    }
}
