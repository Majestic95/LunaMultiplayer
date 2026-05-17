using Lidgren.Network;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Extensions;
using LmpClient.Network;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.Utilities;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using LmpCommon.Time;
using System;

namespace LmpClient.Systems.VesselProtoSys
{
    public class VesselProtoMessageSender : SubSystem<VesselProtoSystem>, IMessageSender
    {
        /// <summary>
        /// Pre allocated array to store the vessel data into it. Max 10 megabytes
        /// </summary>
        private static readonly byte[] VesselSerializedBytes = new byte[10 * 1024 * 1000];

        private static readonly object VesselArraySyncLock = new object();

        public void SendMessage(IMessageData msg)
        {
            NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<VesselCliMsg>(msg));
        }

        /// <summary>
        /// BUG-010 Part B: ship a fresh proto for every vessel the local player holds the
        /// Control or Update lock on, just before <see cref="LmpClient.MainSystem.DisconnectFromGame"/>
        /// tears the Lidgren connection down. Pairs with Part A (server-side pin broadcast on
        /// disconnect detection) to ensure the server's on-disk vessel snapshot reflects the
        /// actual moment-of-disconnect pose instead of whatever was captured by the last
        /// periodic proto broadcast (default cadence is ~30s). Matters for the dock-then-logoff
        /// case: when the remaining player later undocks, the new child vessel reconstructs
        /// from the on-disk proto and inherits the canonical pose.
        ///
        /// Synchronous: serializes each proto on the Unity main thread and pushes the bytes
        /// straight to Lidgren via <c>ClientConnection.SendMessage</c>, then calls
        /// <c>FlushSendQueue</c> once at the end. Skips the normal async path
        /// (<see cref="SendVesselMessage"/> → <c>TaskFactory.StartNew</c> →
        /// <see cref="NetworkSender.OutgoingMessages"/>) because we cannot afford the
        /// enqueue→drain→flush race against <c>NetworkConnection.Disconnect</c>'s
        /// queue-wipe that follows microseconds later. Caller must be on the Unity main thread
        /// (Lingoona crashes if proto serialization runs off-thread, per the historical comment
        /// on <see cref="PrepareAndSendProtoVessel"/>).
        ///
        /// Only fires on clean disconnects (user clicks Disconnect / Quit-to-Menu); ungraceful
        /// drops never reach this code path and continue to rely on Part A's server-side
        /// broadcast alone. Spectators and not-yet-connected clients short-circuit.
        /// </summary>
        public int SendOwnedVesselsForDisconnect(string reason)
        {
            if (VesselCommon.IsSpectating || FlightGlobals.Vessels == null) return 0;
            if (NetworkMain.ClientConnection == null
                || NetworkMain.ClientConnection.Status != NetPeerStatus.Running
                || MainSystem.NetworkState < ClientState.Connected) return 0;

            var localPlayer = SettingsSystem.CurrentSettings.PlayerName;
            var sent = 0;
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (vessel == null || vessel.state == Vessel.State.DEAD) continue;
                if (vessel.id == Guid.Empty) continue;
                if (VesselRemoveSystem.Singleton.VesselWillBeKilled(vessel.id)) continue;
                if (!vessel.orbitDriver || !vessel.orbitDriver.Ready()) continue;

                var ownsLock = LockSystem.LockQuery.ControlLockBelongsToPlayer(vessel.id, localPlayer)
                            || LockSystem.LockQuery.UpdateLockBelongsToPlayer(vessel.id, localPlayer);
                if (!ownsLock) continue;

                if (SendVesselProtoSynchronously(vessel, reason)) sent++;
            }

            if (sent > 0)
            {
                NetworkMain.ClientConnection?.FlushSendQueue();
            }
            return sent;
        }

        /// <summary>
        /// Disconnect-flush helper. Builds the ProtoVessel, serializes, hands the bytes to
        /// Lidgren — all synchronously on the calling (Unity main) thread. Returns true if a
        /// message was queued for Lidgren. Mirrors <see cref="PrepareAndSendProtoVessel"/>'s
        /// shape but with the wire-handoff inlined; the existing async path stays untouched
        /// for periodic broadcasts where main-thread cost would be a frame-rate hit.
        /// </summary>
        private bool SendVesselProtoSynchronously(Vessel vessel, string reason)
        {
            var protoVessel = vessel.BackupVessel();
            if (protoVessel == null || protoVessel.vesselID == Guid.Empty) return false;

            lock (VesselArraySyncLock)
            {
                VesselSerializer.SerializeVesselToArray(protoVessel, VesselSerializedBytes, out var numBytes);
                if (numBytes <= 0) return false;

                var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<VesselProtoMsgData>();
                msgData.GameTime = TimeSyncSystem.UniversalTime;
                msgData.VesselId = protoVessel.vesselID;
                msgData.NumBytes = numBytes;
                msgData.ForceReload = false;
                msgData.Reason = reason;
                if (msgData.Data.Length < numBytes)
                    Array.Resize(ref msgData.Data, numBytes);
                Array.Copy(VesselSerializedBytes, 0, msgData.Data, 0, numBytes);

                var msg = MessageFactory.CreateNew<VesselCliMsg>(msgData);
                msg.Data.SentTime = LunaNetworkTime.UtcNow.Ticks;
                var outgoing = NetworkMain.ClientConnection.CreateMessage(msg.GetMessageSize());
                msg.Serialize(outgoing);
                NetworkMain.ClientConnection.SendMessage(outgoing, msg.NetDeliveryMethod, msg.Channel);
                msg.Recycle();
                return true;
            }
        }

        public void SendVesselMessage(Vessel vessel, bool forceReload = false, string reason = null)
        {
            if (vessel == null || vessel.state == Vessel.State.DEAD || VesselRemoveSystem.Singleton.VesselWillBeKilled(vessel.id))
                return;

            if (!vessel.orbitDriver)
            {
                LunaLog.LogWarning($"Cannot send vessel {vessel.vesselName} - {vessel.id}. It's orbit driver is null!");
                return;
            }

            if (vessel.orbitDriver.Ready())
            {
                vessel.protoVessel = vessel.BackupVessel();
                SendVesselMessage(vessel.protoVessel, forceReload, reason);
            }
            else
            {
                //Orbit driver is not ready so wait max 10 frames until it's ready
                CoroutineUtil.StartConditionRoutine("SendVesselMessage",
                    () => SendVesselMessage(vessel, forceReload, reason),
                    () => vessel.orbitDriver.Ready(), 10);
            }
        }

        #region Private methods

        private void SendVesselMessage(ProtoVessel protoVessel, bool forceReload, string reason)
        {
            if (protoVessel == null || protoVessel.vesselID == Guid.Empty) return;
            //Doing this in another thread can crash the game as during the serialization into a config node Lingoona is called...
            //TODO: Check if this works fine with the new unity version as it used to crash....
            TaskFactory.StartNew(() => PrepareAndSendProtoVessel(protoVessel, forceReload, reason));
            //PrepareAndSendProtoVessel(protoVessel);
        }

        /// <summary>
        /// This method prepares the protovessel class and send the message, it's intended to be run in another thread
        /// </summary>
        private void PrepareAndSendProtoVessel(ProtoVessel protoVessel, bool forceReload, string reason)
        {
            //Never send empty vessel id's (it happens with flags...)
            if (protoVessel.vesselID == Guid.Empty) return;

            //VesselSerializedBytes is shared so lock it!
            lock (VesselArraySyncLock)
            {
                VesselSerializer.SerializeVesselToArray(protoVessel, VesselSerializedBytes, out var numBytes);
                if (numBytes > 0)
                {
                    var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<VesselProtoMsgData>();
                    msgData.GameTime = TimeSyncSystem.UniversalTime;
                    msgData.VesselId = protoVessel.vesselID;
                    msgData.NumBytes = numBytes;
                    msgData.ForceReload = forceReload;
                    msgData.Reason = reason;
                    if (msgData.Data.Length < numBytes)
                        Array.Resize(ref msgData.Data, numBytes);
                    Array.Copy(VesselSerializedBytes, 0, msgData.Data, 0, numBytes);

                    SendMessage(msgData);
                }
                else
                {
                    if (protoVessel.vesselType == VesselType.Debris)
                    {
                        LunaLog.Log($"Serialization of debris vessel: {protoVessel.vesselID} name: {protoVessel.vesselName} failed. Adding to kill list");
                        VesselRemoveSystem.Singleton.KillVessel(protoVessel.vesselID, true, "Serialization of debris failed");
                    }
                }
            }
        }

        #endregion
    }
}
