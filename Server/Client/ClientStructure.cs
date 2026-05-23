using Lidgren.Network;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message.Interface;
using Server.Context;
using Server.Plugin;
using Server.Server;
using Server.Settings.Structures;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Client
{
    public class ClientStructure
    {
        public IPEndPoint Endpoint => Connection.RemoteEndPoint;

        public string UniqueIdentifier { get; set; }
        public string KspVersion { get; set; }
        public string LmpVersion { get; set; }

        public bool Authenticated { get; set; }

        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public NetConnection Connection { get; }

        public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Connected;
        public bool DisconnectClient { get; set; }
        public long LastReceiveTime { get; set; } = ServerContext.ServerClock.ElapsedMilliseconds;
        public long LastSendTime { get; set; } = 0;
        public float[] PlayerColor { get; set; } = new float[3];
        public string PlayerName { get; set; } = "Unknown";
        public PlayerStatus PlayerStatus { get; set; } = new PlayerStatus();
        public ConcurrentQueue<IServerMessageBase> SendMessageQueue { get; } = new ConcurrentQueue<IServerMessageBase>();
        public int Subspace { get; set; } = int.MinValue; //Leave it as min value. When client connect we force them client side to go to latest subspace
        public float SubspaceRate { get; set; } = 1f;

        /// <summary>
        /// Phase 2 of server-side-offload — most-recently reported active vessel id for
        /// this client. Set in <see cref="Server.Message.VesselMsgReader.HandleMessage"/>'s
        /// Flightstate case (Flightstate is by design the local-active-vessel-only path —
        /// <see cref="LmpClient.Systems.VesselFlightStateSys.VesselFlightStateSystem.SendFlightState"/>
        /// calls SendCurrentFlightState only for the active vessel). Drives the
        /// recipient-side resolution in <see cref="MessageQueuer.ResolveRecipientBody"/>
        /// for the same-body filter — when a Position relay arrives, the server matches
        /// the sender's vessel body against the recipient's active-vessel body and drops
        /// the relay if they're at different celestial bodies. <see cref="Guid.Empty"/>
        /// = "client hasn't sent first Flightstate yet" → same-body filter falls back to
        /// permissive (relay always). Synchronously written on the Lidgren receive
        /// thread; read by the same thread during the next relay decision (no race).
        /// </summary>
        public Guid ActiveVesselId { get; set; } = Guid.Empty;

        /// <summary>
        /// Phase 2 of server-side-offload — body name of <see cref="ActiveVesselId"/>'s
        /// orbit. Updated in <see cref="Server.Message.VesselMsgReader.HandleMessage"/>'s
        /// Position case when the inbound Position's VesselId matches
        /// <see cref="ActiveVesselId"/>. Synchronous fast path so the same-body filter
        /// at relay time doesn't have to round-trip through <c>VesselStoreSystem</c> +
        /// <c>Vessel.GetOrbitingBodyName</c> (which is eventually-consistent — populated
        /// by the 2.5s-throttled <see cref="Server.System.Vessel.VesselDataUpdater.WritePositionDataToFile"/>
        /// path). <c>null</c>/empty = "recipient body unknown" → filter goes permissive.
        /// </summary>
        public string ActiveVesselBodyName { get; set; }

        public DateTime ConnectionTime { get; } = DateTime.UtcNow;

        public Task SendThread { get; }

        public ClientStructure(NetConnection playerConnection)
        {
            Connection = playerConnection;
            SendThread = MainServer.LongRunTaskFactory.StartNew(() => SendMessagesThreadAsync(MainServer.CancellationTokenSrc.Token), MainServer.CancellationTokenSrc.Token);
        }

        public override bool Equals(object obj)
        {
            var clientToCompare = obj as ClientStructure;
            return Endpoint.Equals(clientToCompare?.Endpoint);
        }

        public override int GetHashCode()
        {
            return Endpoint?.GetHashCode() ?? 0;
        }

        private const int MaxMessagesPerBatch = 128;

        private async Task SendMessagesThreadAsync(CancellationToken token)
        {
            while (ConnectionStatus == ConnectionStatus.Connected)
            {
                var sentCount = 0;
                while (sentCount < MaxMessagesPerBatch && SendMessageQueue.TryDequeue(out var message) && message != null)
                {
                    try
                    {
                        LidgrenServer.SendMessageToClient(this, message);
                        sentCount++;
                    }
                    catch (Exception e)
                    {
                        ClientException.HandleDisconnectException("Send network message error: ", this, e);
                        return;
                    }

                    LmpPluginHandler.FireOnMessageSent(this, message);
                }

                if (sentCount > 0)
                {
                    LidgrenServer.FlushSendQueue();
                    continue;
                }

                try
                {
                    await Task.Delay(IntervalSettings.SettingsStore.SendReceiveThreadTickMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }
}
