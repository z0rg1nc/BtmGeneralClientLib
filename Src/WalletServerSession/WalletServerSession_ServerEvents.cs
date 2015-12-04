using System;
using System.Linq;
using System.Reactive.Subjects;
using BtmI2p.GeneralClientInterfaces.WalletServer;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.BitMoneyClient.Lib.WalletServerSession
{
    public class WalletClientEventObject
    {
        public Guid EventGuid;
        public EWalletClientEventTypes EventType;
        public object EventArgs;
        public DateTime RaisedDateTime;
    }
    public partial class WalletServerSession
    {
        private readonly Subject<WalletClientEventObject> _serverEvents
            = new Subject<WalletClientEventObject>();
        public IObservable<WalletClientEventObject> ServerEvents => _serverEvents;
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
                                var request = new WalletSubscribeClientEventsRequest()
                                {
                                    StartTime = _initTime,
                                    LastKnownEventGuid = currentLastKnownEventGuid,
                                    TimeoutSeconds = (int) _settings.SubscribeTimeout.TotalSeconds,
                                    MaxBufferCount = _settings.BufferCount,
                                    MaxBufferSeconds = (int) _settings.BufferTime.TotalSeconds
                                };
                                return await _authedWalletInterface.SubscribeClientEvents(
                                    request
                                ).ConfigureAwait(false);
                            },
                            _cts.Token
                        ).ConfigureAwait(false);
                        if (newEvents.Any())
                        {
                            foreach (var walletClientEvent in newEvents)
                            {
                                _serverEvents.OnNext(
                                    new WalletClientEventObject()
                                    {
                                        EventType = walletClientEvent.EventType,
                                        EventGuid = walletClientEvent.EventGuid,
                                        RaisedDateTime = walletClientEvent.RaisedDateTime,
                                        EventArgs = walletClientEvent.SerializedEventArgs.ParseJsonToType(
                                            WalletClientEventSerialized.GetEventArgsType(
                                                walletClientEvent.EventType
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
