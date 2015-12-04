using System;
using System.Linq;
using System.Reactive.Subjects;
using BtmI2p.GeneralClientInterfaces.MessageServer;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.BitMoneyClient.Lib.MessageServerSession
{
    public partial class MessageServerSession
    {
        private class MessageClientEventObject
        {
            public Guid EventGuid;
            public EMessageClientEventTypes EventType;
            public object EventArgs;
            public DateTime RaisedDateTime;
        }
        private readonly Subject<MessageClientEventObject> _serverEvents
            = new Subject<MessageClientEventObject>();

        private IObservable<MessageClientEventObject> ServerEvents => _serverEvents;

        private async void FeedServerEventsAction()
        {
            try
            {
                var lastKnownEventGuid = Guid.Empty;
                while (!_cts.IsCancellationRequested)
                {
                    using (_stateHelper.GetFuncWrapper())
                    {
                        var currentLastKnownEventGuid = lastKnownEventGuid;
                        var newEvents = await MiscFuncs.RepeatWhileTimeout(
                            async () =>
                            {
                                var request = new MessageSubscribeClientEventsRequest()
                                {
                                    StartTime = _initTime,
                                    LastKnownEventGuid = currentLastKnownEventGuid,
                                    TimeoutSeconds = (int)_settings.SubscribeTimeout.TotalSeconds,
                                    MaxBufferCount = _settings.BufferCount,
                                    MaxBufferSeconds = (int)_settings.BufferTime.TotalSeconds
                                };
                                return await _authedMessageInterface.SubscribeClientEvents(
                                    request
                                ).ConfigureAwait(false);
                            },
                            _cts.Token
                        ).ConfigureAwait(false);
                        if (newEvents.Any())
                        {
                            foreach (var messageClientEvent in newEvents)
                            {
                                _serverEvents.OnNext(
                                    new MessageClientEventObject()
                                    {
                                        EventType = messageClientEvent.EventType,
                                        EventGuid = messageClientEvent.EventGuid,
                                        RaisedDateTime = messageClientEvent.RaisedDateTime,
                                        EventArgs = messageClientEvent.SerializedEventArgs.ParseJsonToType(
                                            MessageClientEventSerialized.GetEventArgsType(
                                                messageClientEvent.EventType
                                            )
                                        )
                                    }
                                );
                            }
                            lastKnownEventGuid = newEvents.Last().EventGuid;
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
    }
}
