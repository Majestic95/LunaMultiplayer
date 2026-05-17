using Lidgren.Network;
using LmpCommon.Message.Interface;
using LmpCommon.Time;
using Server.Context;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace MockClientTest.Harness
{
    /// <summary>
    /// Thin Lidgren <see cref="NetClient"/> wrapper that speaks the LMP wire
    /// protocol. Uses the production <see cref="ServerContext.ClientMessageFactory"/>
    /// for outbound serialization and <see cref="ServerContext.ServerMessageFactory"/>
    /// for inbound deserialization — same code paths as the real client, just
    /// without the KSP-bound LmpClient assembly in the loop.
    ///
    /// Designed for tests: instantiate, <see cref="Connect"/>, exchange
    /// messages via <see cref="SendMessage{T}"/> / <see cref="WaitForReply{TData}"/>,
    /// then dispose. Multiple mock clients can coexist in the same test as
    /// long as each gets its own instance.
    /// </summary>
    public sealed class MockNetClient : IDisposable
    {
        private readonly NetClient _client;
        private readonly BlockingCollection<IMessageBase> _inbox = new BlockingCollection<IMessageBase>(boundedCapacity: 1024);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _receiveTask;
        private bool _disposed;

        public MockNetClient()
        {
            // Mirror the AppId the server uses (ServerContext.Config) — Lidgren
            // refuses any peer that doesn't match.
            var cfg = new NetPeerConfiguration("LMP")
            {
                AutoFlushSendQueue = true,
                ConnectionTimeout = 10f,
                PingInterval = 2f,
            };
            _client = new NetClient(cfg);
            _client.Start();
        }

        /// <summary>
        /// Connects to the in-process harness server and blocks until Lidgren
        /// reports <see cref="NetConnectionStatus.Connected"/> or times out.
        /// </summary>
        public bool Connect(int port, TimeSpan timeout)
        {
            _client.Connect("127.0.0.1", port);
            _receiveTask = Task.Run(ReceiveLoop);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_client.ServerConnection != null &&
                    _client.ServerConnection.Status == NetConnectionStatus.Connected)
                    return true;
                Thread.Sleep(20);
            }
            return false;
        }

        /// <summary>
        /// Wraps <paramref name="data"/> in a <typeparamref name="TMsg"/> via the
        /// shared <see cref="ServerContext.ClientMessageFactory"/>, serializes it,
        /// and ships it. <typeparamref name="TMsg"/> must be a client-side
        /// message wrapper (e.g., <c>HandshakeCliMsg</c>) registered in
        /// <see cref="ClientMessageType"/>.
        /// </summary>
        public void SendMessage<TMsg>(IMessageData data) where TMsg : class, IClientMessageBase
        {
            if (_client.ServerConnection == null)
                throw new InvalidOperationException("MockNetClient is not connected.");

            var msg = ServerContext.ClientMessageFactory.CreateNew<TMsg>(data);
            var outmsg = _client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(outmsg);
            _client.SendMessage(outmsg, _client.ServerConnection, msg.NetDeliveryMethod, msg.Channel);
        }

        /// <summary>
        /// Drains the inbox until a message whose <c>Data</c> is of type
        /// <typeparamref name="TData"/> arrives, then returns that data.
        /// Returns null on timeout. Other inbound messages are dropped on
        /// the floor — the harness is not (yet) a stateful replay log.
        /// </summary>
        public TData WaitForReply<TData>(TimeSpan timeout) where TData : class, IMessageData
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                if (_inbox.TryTake(out var msg, remaining))
                {
                    if (msg.Data is TData typed)
                        return typed;
                    // Not the type we want — discard and keep waiting.
                }
            }
            return null;
        }

        private void ReceiveLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                NetIncomingMessage msg;
                try
                {
                    msg = _client.ReadMessage();
                }
                catch
                {
                    break;
                }
                if (msg == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                switch (msg.MessageType)
                {
                    case NetIncomingMessageType.Data:
                        try
                        {
                            var deserialized = ServerContext.ServerMessageFactory.Deserialize(msg, LunaNetworkTime.UtcNow.Ticks);
                            if (deserialized != null)
                                _inbox.Add(deserialized);
                        }
                        catch
                        {
                            // Bad payload — drop. A future harness v2 can record
                            // these for diagnostic dumps.
                        }
                        break;
                    case NetIncomingMessageType.StatusChanged:
                    case NetIncomingMessageType.WarningMessage:
                    case NetIncomingMessageType.ErrorMessage:
                    case NetIncomingMessageType.DebugMessage:
                    case NetIncomingMessageType.VerboseDebugMessage:
                    default:
                        break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { }
            try { _client.Disconnect("test done"); } catch { }
            try { _receiveTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            try { _client.Shutdown("test done"); } catch { }
            _inbox.Dispose();
            _cts.Dispose();
        }
    }
}
