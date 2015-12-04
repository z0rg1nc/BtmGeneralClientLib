using System;
using System.Linq;
using System.Reactive.Subjects;
using BtmI2p.GeneralClientInterfaces.ExchangeServer;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.BitMoneyClient.Lib.ExchangeServerSession
{
    public partial class ExchangeServerSession
    {
        private class ExchangeClientEventObject
        {
            public Guid EventGuid;
            public EExchangeClientEventTypes EventType;
            public object EventArgs;
            public DateTime RaisedDateTime;
        }
        private readonly Subject<ExchangeClientEventObject> _serverEvents
            = new Subject<ExchangeClientEventObject>();
        private IObservable<ExchangeClientEventObject> ServerEvents => _serverEvents;

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
                                var request = new ExchangeSubscribeClientEventsRequest()
                                {
                                    LastKnownEventGuid = currentLastKnownEventGuid,
                                    StartTime = _initTime
                                };
                                return await _authenticatedExchangeInterface.SubscribeClientEvents(
                                    request
                                ).ConfigureAwait(false);
                            },
                            _cts.Token
                        ).ConfigureAwait(false);
                        if (newEvents.Any())
                        {
                            foreach (var exchangeClientEvent in newEvents)
                            {
                                _serverEvents.OnNext(
                                    new ExchangeClientEventObject()
                                    {
                                        EventGuid = exchangeClientEvent.EventGuid,
                                        EventType = exchangeClientEvent.EventType,
                                        RaisedDateTime = exchangeClientEvent.RaisedDateTime,
                                        EventArgs = exchangeClientEvent.SerializedEventArgs.ParseJsonToType(
                                            ExchangeClientEventSerialized.GetEventArgsType(
                                                exchangeClientEvent.EventType
                                            )
                                        )
                                    }
                                );
#if DEBUG
                                /*
								_logger.Trace(
									"Server event {1} {0}", 
									exchangeClientEvent.WriteObjectToJson(),
									exchangeClientEvent.EventType
								);
                                */
#endif
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
                HandleUnexpectedError(exc);
            }
        }
    }
}
