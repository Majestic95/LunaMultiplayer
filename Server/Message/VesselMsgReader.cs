using ByteSizeLib;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Message.Base;
using Server.Server;
using Server.System;
using Server.System.Vessel;
using System;
using System.Linq;
using System.Text;

namespace Server.Message
{
    public class VesselMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var messageData = message.Data as VesselBaseMsgData;
            switch (messageData?.VesselMessageType)
            {
                case VesselMessageType.Sync:
                    HandleVesselsSync(client, messageData);
                    message.Recycle();
                    break;
                case VesselMessageType.Proto:
                    HandleVesselProto(client, messageData);
                    break;
                case VesselMessageType.Remove:
                    HandleVesselRemove(client, messageData);
                    break;
                //[perf:relay-scene Phase 1] Continuous vessel-state relays use
                //RelayMessageToFlightScene so recipients not in Flight/TrackingStation
                //drop the message at the relay decision (no SendToClient → no
                //serialization → no main-thread receive-queue depth). Catch-up /
                //structural relays (Proto / Sync / Couple / Remove handled below)
                //stay on the unconditional RelayMessage path — they must populate
                //FlightGlobals.Vessels in EVERY scene so scene entry into Flight
                //finds a fully populated world.
                case VesselMessageType.Position:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    //[perf:relay-body Phase 2] If this Position is for the sender's
                    //tracked active vessel, snapshot the body name onto ClientStructure
                    //so the same-body filter at relay time can read RECIPIENT bodies
                    //(every recipient's own ActiveVesselBodyName is set the same way
                    //from their own prior Position updates).
                    //
                    //Sender's active VesselId is captured below in the Flightstate case
                    //(Flightstate is the local-active-vessel-only message per
                    //VesselFlightStateSystem.SendFlightState).
                    //
                    //Ordering note (Phase 2 review S1): this write happens BEFORE the
                    //relay below; the just-updated value is not consumed by THIS
                    //message's relay (which reads recipients' bodies, never the
                    //sender's) — it's prepared for the NEXT inbound Position from a
                    //different sender that targets this client as recipient.
                    //
                    //Thread-safe: written + read on the same Lidgren receive thread
                    //per LidgrenServer.StartReceivingMessagesAsync (single-threaded
                    //sequential dispatch).
                    if (messageData is VesselPositionMsgData posMsg)
                    {
                        if (posMsg.VesselId == client.ActiveVesselId)
                            client.ActiveVesselBodyName = posMsg.BodyName;
                        //[perf:relay-cadence Phase 3] Position has its own composed
                        //relay entry point that adds the per-vessel cadence throttle
                        //on top of the Phase 1 + Phase 2 filters. Other vessel-state
                        //messages (Flightstate / Update / etc.) stay on
                        //RelayMessageToFlightSceneSameBody — they don't need cadence
                        //shaping (their baseline cadence is already low: 1500ms / 5000ms).
                        MessageQueuer.RelayPositionMessage<VesselSrvMsg>(client, posMsg);
                    }
                    if (client.Subspace == WarpContext.LatestSubspace.Id)
                        VesselDataUpdater.WritePositionDataToFile(messageData);
                    break;
                case VesselMessageType.Flightstate:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    //[perf:relay-body Phase 2] Capture the sender's active vessel id —
                    //Flightstate by design is sent only for the local active vessel
                    //(LmpClient.Systems.VesselFlightStateSys.VesselFlightStateSystem.SendFlightState
                    //calls SendCurrentFlightState only for ActiveVessel). On the next
                    //Position for this vessel, we'll cache the body name onto the
                    //client structure for fast same-body filter lookups.
                    client.ActiveVesselId = messageData.VesselId;
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    VesselDataUpdater.WriteFlightstateDataToFile(messageData);
                    break;
                case VesselMessageType.Update:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    VesselDataUpdater.WriteUpdateDataToFile(messageData);
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Resource:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    VesselDataUpdater.WriteResourceDataToFile(messageData);
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncField:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    VesselDataUpdater.WritePartSyncFieldDataToFile(messageData);
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncUiField:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    VesselDataUpdater.WritePartSyncUiFieldDataToFile(messageData);
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncCall:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.ActionGroup:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    VesselDataUpdater.WriteActionGroupDataToFile(messageData);
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Fairing:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    VesselDataUpdater.WriteFairingDataToFile(messageData);
                    MessageQueuer.RelayMessageToFlightSceneSameBody<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Decouple:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    //Structural: recipients need to know the vessel was decoupled
                    //regardless of scene so their CurrentVessels stays in sync.
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Couple:
                    HandleVesselCouple(client, messageData);
                    break;
                case VesselMessageType.Undock:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    //Structural (see Decouple comment).
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                default:
                    throw new NotImplementedException("Vessel message type not implemented");
            }
        }

        /// <summary>
        /// BUG-005/006 widening: returns true (caller must drop the message) when the sending
        /// client is in a subspace strictly past the message's target vessel's recorded
        /// AuthoritativeSubspaceId. <see cref="HandleVesselProto"/> already enforces this for
        /// proto-updates; the per-message-type relay paths above need the same gate because
        /// the lock-acquire-time check in <see cref="LockSystem.AcquireLock"/> doesn't survive
        /// a subspace change made after the lock was acquired (a client holding an
        /// UnloadedUpdate lock that then warps to a past subspace would otherwise broadcast
        /// stale state into the future-subspace's timeline).
        ///
        /// Vessels not yet in the store (first-time-seen ids) fall through to the legitimate
        /// insert/relay path — the auth check needs an existing record to compare against.
        /// </summary>
        private static bool RejectIfPastSubspace(ClientStructure client, VesselBaseMsgData data)
        {
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(data.VesselId, out var existing))
                return false;
            if (!WarpSystem.IsStrictlyPast(client.Subspace, existing.AuthoritativeSubspaceId))
                return false;

            LunaLog.Debug($"[fix:BUG-005/006] rejecting {data.VesselMessageType} for {data.VesselId} from {client.PlayerName} " +
                          $"(client subspace {client.Subspace} is past vessel authority subspace {existing.AuthoritativeSubspaceId})");
            return true;
        }

        private static void HandleVesselRemove(ClientStructure client, VesselBaseMsgData message)
        {
            var data = (VesselRemoveMsgData)message;

            if (LockSystem.LockQuery.ControlLockExists(data.VesselId) && !LockSystem.LockQuery.ControlLockBelongsToPlayer(data.VesselId, client.PlayerName))
                return;

            if (VesselStoreSystem.VesselExists(data.VesselId))
            {
                LunaLog.Debug($"Removing vessel {data.VesselId} from {client.PlayerName}");
                VesselStoreSystem.RemoveVessel(data.VesselId);
            }

            if (data.AddToKillList)
                VesselContext.RemovedVessels.TryAdd(data.VesselId, 0);

            //Relay the message.
            MessageQueuer.RelayMessage<VesselSrvMsg>(client, data);
        }

        private static void HandleVesselProto(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselProtoMsgData)message;

            if (VesselContext.RemovedVessels.ContainsKey(msgData.VesselId)) return;

            if (msgData.NumBytes == 0)
            {
                LunaLog.Warning($"Received a vessel with 0 bytes ({msgData.VesselId}) from {client.PlayerName}.");
                return;
            }

            //BUG-005/006: synchronously reject proto-updates from a client whose subspace is
            //strictly past the vessel's current AuthoritativeSubspaceId. The store update and
            //the relay are both suppressed so other clients do not see the rewound state.
            if (VesselStoreSystem.CurrentVessels.TryGetValue(msgData.VesselId, out var existing)
                && WarpSystem.IsStrictlyPast(client.Subspace, existing.AuthoritativeSubspaceId))
            {
                LunaLog.Debug($"[fix:BUG-005/006] rejecting proto-update for {msgData.VesselId} from {client.PlayerName} " +
                              $"(client subspace {client.Subspace} is past vessel authority subspace {existing.AuthoritativeSubspaceId})");
                return;
            }

            if (!VesselStoreSystem.VesselExists(msgData.VesselId))
            {
                LunaLog.Debug($"Saving vessel {msgData.VesselId} ({ByteSize.FromBytes(msgData.NumBytes).KiloBytes} KB) from {client.PlayerName}.");
            }

            VesselDataUpdater.RawConfigNodeInsertOrUpdate(msgData.VesselId, Encoding.UTF8.GetString(msgData.Data, 0, msgData.NumBytes), client.Subspace);
            MessageQueuer.RelayMessage<VesselSrvMsg>(client, msgData);
        }

        private static void HandleVesselsSync(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselSyncMsgData)message;

            var allVessels = VesselStoreSystem.CurrentVessels.Keys.ToList();

            //Here we only remove the vessels that the client ALREADY HAS so we only send the vessels they DON'T have
            for (var i = 0; i < msgData.VesselsCount; i++)
                allVessels.Remove(msgData.VesselIds[i]);

            var vesselsToSend = allVessels;
            foreach (var vesselId in vesselsToSend)
            {
                var vesselData = VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId);
                if (vesselData.Length > 0)
                {
                    var protoMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                    var vesselBytes = Encoding.UTF8.GetBytes(vesselData);
                    protoMsg.Data = vesselBytes;
                    protoMsg.NumBytes = vesselBytes.Length;
                    protoMsg.VesselId = vesselId;

                    MessageQueuer.SendToClient<VesselSrvMsg>(client, protoMsg);
                }
            }

            if (allVessels.Count > 0)
                LunaLog.Debug($"Sending {client.PlayerName} {vesselsToSend.Count} vessels");
        }

        private static void HandleVesselCouple(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselCoupleMsgData)message;

            //BUG-005/006 (retro S3): a couple is destructive — it removes the weaker vessel,
            //rewrites the dominant's AuthoritativeSubspaceId, and broadcasts a remove to all
            //clients. A past-subspace initiator must not perform any of those mutations against
            //a future-subspace vessel. Reject the entire couple BEFORE the relay so other
            //clients never see the stale message.
            if (VesselStoreSystem.CurrentVessels.TryGetValue(msgData.VesselId, out var existingDominant)
                && WarpSystem.IsStrictlyPast(client.Subspace, existingDominant.AuthoritativeSubspaceId))
            {
                LunaLog.Debug($"[fix:BUG-005/006] rejecting Couple for {msgData.VesselId} from {client.PlayerName} " +
                              $"(client subspace {client.Subspace} is past dominant vessel authority subspace {existingDominant.AuthoritativeSubspaceId})");
                return;
            }

            LunaLog.Debug($"Coupling message received! Dominant vessel: {msgData.VesselId}");
            MessageQueuer.RelayMessage<VesselSrvMsg>(client, msgData);

            if (VesselContext.RemovedVessels.ContainsKey(msgData.CoupledVesselId)) return;

            //BUG-005/006: initiator-wins handoff. The dominant vessel that survives the couple
            //inherits the initiating client's subspace as its new AuthoritativeSubspaceId so the
            //merged vessel's lock semantics follow the player who performed the action.
            //See docs/research/02-analysis/bug-005-006-cross-subspace-lock.md.
            if (client.Subspace > 0
                && VesselStoreSystem.CurrentVessels.TryGetValue(msgData.VesselId, out var dominantVessel))
            {
                dominantVessel.AuthoritativeSubspaceId = client.Subspace;
            }

            //Now remove the weak vessel but DO NOT add to the removed vessels as they might undock!!!
            LunaLog.Debug($"Removing weak coupled vessel {msgData.CoupledVesselId}");
            VesselStoreSystem.RemoveVessel(msgData.CoupledVesselId);

            //Tell all clients to remove the weak vessel
            var removeMsgData = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
            removeMsgData.VesselId = msgData.CoupledVesselId;

            MessageQueuer.SendToAllClients<VesselSrvMsg>(removeMsgData);
        }
    }
}
