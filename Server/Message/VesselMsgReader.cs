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
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
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
                case VesselMessageType.Position:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    if (client.Subspace == WarpContext.LatestSubspace.Id)
                        VesselDataUpdater.WritePositionDataToFile(messageData);
                    break;
                case VesselMessageType.Flightstate:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    VesselDataUpdater.WriteFlightstateDataToFile(messageData);
                    break;
                case VesselMessageType.Update:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    VesselDataUpdater.WriteUpdateDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Resource:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    VesselDataUpdater.WriteResourceDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncField:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    VesselDataUpdater.WritePartSyncFieldDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncUiField:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    VesselDataUpdater.WritePartSyncUiFieldDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.PartSyncCall:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.ActionGroup:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    VesselDataUpdater.WriteActionGroupDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Fairing:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    VesselDataUpdater.WriteFairingDataToFile(messageData);
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Decouple:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
                    MessageQueuer.RelayMessage<VesselSrvMsg>(client, messageData);
                    break;
                case VesselMessageType.Couple:
                    HandleVesselCouple(client, messageData);
                    break;
                case VesselMessageType.Undock:
                    if (RejectIfPastSubspace(client, messageData)) break;
                    if (RejectIfCrossAgencyWrite(client.PlayerName, messageData)) break;
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

        /// <summary>
        /// [Stage 5.17a write-path counterpart, session 19 soak Finding 2] Returns true
        /// (caller must drop the message — no relay, no disk write) when the sender's
        /// agency is not the owning agency of the target vessel. <see cref="LockSystem.AcquireLock"/>
        /// already closes the lock-acquire hole, but the vessel-relay path was previously
        /// unconditional — a cross-agency player could broadcast Position / Flightstate /
        /// Update / Resource / PartSync* / ActionGroup / Fairing / Decouple / Undock for
        /// any vessel they had the id of, regardless of lock state. KSP's tracking-station
        /// "Fly" loads another player's vessel into the local Flight scene; the relayed
        /// (unauthorised) state then collides with the owning agency's authoritative
        /// simulation = physics jitter on the owner's instance.
        ///
        /// Also called from <see cref="HandleVesselRemove"/> and <see cref="HandleVesselCouple"/>
        /// to close two destructive holes the consumer-lens + server-systems reviews
        /// caught: Remove was only gated on ControlLockExists (and not gated at all when
        /// no Control lock was held — Alice's pinned-but-unlocked vessel could be deleted
        /// by cross-agency Bob); Couple rewrites the dominant's AuthoritativeSubspaceId
        /// and removes the weak vessel without any cross-agency check.
        ///
        /// [v4-proto-write-guard, session 39] ALSO called from <see cref="HandleVesselProto"/>.
        /// The session-19 ship of this helper covered the 11 relayed types + Remove + Couple
        /// but omitted the proto handler — the omission was rationalised by the "relayed proto
        /// bytes are advisory" framing in the [[5.18b relay-vs-store note]], but that framing
        /// concerned peer-client interpretation of the relay surface only. The server's
        /// authoritative store DID get overwritten by the proto bytes via
        /// <see cref="Server.System.Vessel.VesselDataUpdater.RawConfigNodeInsertOrUpdate"/>;
        /// the 5.16b stamp-preservation logic preserves only <see cref="Server.System.Vessel.Classes.Vessel.OwningAgencyId"/>,
        /// not crew list / parts / resources / position. The proto-path call closes that
        /// broad exploit class. Same bypass-cases apply; vessel-not-in-store is the CORRECT
        /// default for the proto path because proto is the legitimate entry point for new
        /// vessels and the 5.16b stamp logic at <c>VesselDataUpdater.cs:152-154</c> auto-
        /// routes them to the sender's own agency. See
        /// <c>docs/research/v4-vessel-proto-cross-agency-write-guard.md</c> for the full
        /// threat model + the documented race-craft-pre-create limitation (§3.a).
        ///
        /// Bypass-only cases (mirror <see cref="LockSystem.AcquireLock"/> Stage 5.17a):
        ///   - Gate off: dual-mode silence (spec §11).
        ///   - Vessel not in store: first-time-seen ids fall through. Unlike 5.17a's
        ///     lock-acquire defense this does NOT reject under gate=on — the ingest
        ///     race is asymmetric on the relay path. A peer would need to know the
        ///     vessel id to broadcast a position update, which means they've already
        ///     received the relayed proto bytes; a legitimate KSP client wouldn't
        ///     broadcast vessel state without holding a lock anyway, so the rare race
        ///     is a single dropped tick at worst.
        ///   - <c>Vessel.OwningAgencyId == Guid.Empty</c>: spec §10 Q3 Unassigned-sentinel;
        ///     any agency may interact until operator <c>transferagency</c> (Stage 5.18d)
        ///     assigns ownership.
        ///   - Sender has no agency mapping: defensive bypass. Production path is safe
        ///     (Authenticated gate runs <see cref="AgencySystem.OnPlayerAuthenticated"/>
        ///     on the same Lidgren receive thread before the player's first vessel
        ///     message can be processed; subsequent CliMsgs always see a populated
        ///     <see cref="AgencySystem.AgencyByPlayerName"/> entry).
        ///
        /// **Why we don't add a lock-bypass branch (defense-in-depth note).** The 5.17a
        /// guard refuses cross-agency lock acquires on the three vessel-scoped lock
        /// types (Control / Update / UnloadedUpdate), so under gate=on it is structurally
        /// impossible for a non-owning player to hold a vessel-scoped lock on another
        /// agency's vessel. The only configuration that could create the situation is
        /// a pre-gate-on lock surviving the gate flip; spec §10 migration is fresh-
        /// start-only (operator archives Universe/ before turning the gate on), so
        /// stale-lock survival is not a supported configuration.
        ///
        /// **Boot-ordering** (upgrade-lens review session 19). <c>LoadExistingVessels</c>
        /// runs in <c>MainServer.Main</c> before <c>LidgrenServer.SetupLidgrenServer</c>
        /// + <c>ServerRunning=true</c>, so the store is fully populated before any
        /// vessel CliMsg can arrive. There is no boot-window where an in-flight vessel
        /// message would bypass the guard due to an empty store.
        ///
        /// **5.18d transferagency forward-note** (server-systems review session 19).
        /// When the admin command lands, it mutates <see cref="AgencySystem.AgencyByPlayerName"/>
        /// on the command thread. The mutation-ordering rule documented at
        /// <see cref="AgencySystem.AgencyByPlayerName"/> requires the index flip to be
        /// the LAST step — until then, this guard reads the pre-transfer mapping and
        /// the transferred player's vessel writes are routed under their old agency,
        /// which is safe (they won't yet be cross-agency against the also-not-yet-
        /// rewritten <see cref="Vessel.OwningAgencyId"/>). transferagency must rewrite
        /// vessel stamps in <see cref="VesselStoreSystem.CurrentVessels"/> AND the
        /// player-name index AND release stale locks atomically — see 5.18d sub-item (e).
        ///
        /// **OwningAgencyId concurrent get/set best-effort** (server-systems review
        /// session 19). <see cref="Vessel.OwningAgencyId"/>'s underlying
        /// <c>MixedCollection&lt;string,string&gt;</c> (LunaConfigNode 1.9.1, no source
        /// in tree) is not documented thread-safe. A torn read during the proto-ingest
        /// <c>Task.Run</c> stamp window would <see cref="Guid.TryParse"/>-fail and
        /// return <see cref="Guid.Empty"/>, which falls into the Unassigned-sentinel
        /// bypass above — a single tick of relay leakage at worst. Same pre-existing
        /// shape as <see cref="LockSystem.AcquireLock"/>'s read at line 93. If a real
        /// race surfaces, snapshot the underlying string once and parse locally.
        /// </summary>
        internal static bool RejectIfCrossAgencyWrite(string playerName, VesselBaseMsgData data)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(data.VesselId, out var existing))
                return false;
            if (existing.OwningAgencyId == Guid.Empty)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(playerName, out var senderAgency))
                return false;
            if (senderAgency == existing.OwningAgencyId)
                return false;

            //Warning-level so operators see the soak-relevant breadcrumb at default log
            //level (consumer + upgrade + server-systems reviewers all flagged session 19).
            //KSP-side a buggy or hostile client at ~25Hz position cadence could spam this;
            //if soak shows the flood is real, rate-limit per (sender, vessel) — keep the
            //flat Warning for now and react if needed.
            LunaLog.Warning($"[fix:per-agency-career] refusing relay of {data.VesselMessageType} for {data.VesselId} from {playerName} " +
                            $"(sender agency {senderAgency:N} != vessel owning agency {existing.OwningAgencyId:N})");
            return true;
        }

        private static void HandleVesselRemove(ClientStructure client, VesselBaseMsgData message)
        {
            var data = (VesselRemoveMsgData)message;

            //[Stage 5.17a write-path counterpart, consumer-lens review session 19] Cross-agency
            //rejection. The existing ControlLockExists check only fires when SOME player holds
            //Control — when no one does (Alice logged off after BUG-010 pinning), a cross-agency
            //Bob's VesselRemoveMsgData would otherwise delete Alice's vessel and broadcast the
            //removal to every peer. Strictly more destructive than the position-jitter the
            //write-path counterpart was originally written to prevent. Bypass shape mirrors
            //the relay-cases helper; under gate=on the agency must match (or vessel is
            //Unassigned-sentinel, or sender has no agency mapping for the defensive fall-
            //through).
            if (RejectIfCrossAgencyWrite(client.PlayerName, data)) return;

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

            //[v4-proto-write-guard] Cross-agency proto-write rejection. The 5.17a write-path
            //counterpart at RejectIfCrossAgencyWrite gates 11 relayed message types + Remove +
            //Couple but was NOT extended to HandleVesselProto when shipped in session 19. The
            //omission was rationalised by the [[5.18b relay-vs-store note]]'s "relayed proto
            //bytes are advisory" framing — but that framing concerns peer-client interpretation
            //of the relay, NOT the server's authoritative store, which DOES get overwritten by
            //the proto bytes. Without this guard, a modified client can craft a proto for
            //another agency's vessel-id (crew list / parts / resources / position / etc.) and
            //the server persists those bytes via RawConfigNodeInsertOrUpdate; the 5.16b stamp-
            //preservation only preserves OwningAgencyId, NOT the rest of the proto payload.
            //
            //Bypass cases mirror the existing helper (gate off / vessel not in store / Empty-
            //sentinel / requester has no agency). Vessel-not-in-store bypass is the correct
            //default for the proto path — proto is the legitimate entry point for new vessels,
            //and the per-agency stamp logic in RawConfigNodeInsertOrUpdate already routes new
            //vessels to the sender's own agency. See docs/research/v4-vessel-proto-cross-agency-
            //write-guard.md §3.a for the documented race-craft-pre-create limitation.
            if (RejectIfCrossAgencyWrite(client.PlayerName, msgData)) return;

            if (!VesselStoreSystem.VesselExists(msgData.VesselId))
            {
                LunaLog.Debug($"Saving vessel {msgData.VesselId} ({ByteSize.FromBytes(msgData.NumBytes).KiloBytes} KB) from {client.PlayerName}.");
            }

            //[Stage 5.16b] Resolve the sender's agency on the receive thread (synchronously),
            //not inside the fire-and-forget Task.Run body of RawConfigNodeInsertOrUpdate.
            //AgencyByPlayerName mutates under Stage 5.18d's `transferagency` admin command;
            //snapshotting here pins the sender's agency at proto-receipt time, which is the
            //right semantics ("the player owning this proto belongs to agency X at the moment
            //the wire bytes arrived"). Guid.Empty means "don't stamp" (gate off, or sender
            //has no agency yet — only possible if RegisterAgency hasn't run for them).
            var senderAgencyId = Guid.Empty;
            if (AgencySystem.PerAgencyEnabled
                && AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var resolved))
            {
                senderAgencyId = resolved;
            }

            VesselDataUpdater.RawConfigNodeInsertOrUpdate(msgData.VesselId, Encoding.UTF8.GetString(msgData.Data, 0, msgData.NumBytes), client.Subspace, senderAgencyId);

            //[Stage 5.16b — round-3 persistence review] Relay-vs-store divergence: the
            //relayed bytes below are the ORIGINAL wire bytes the sending client supplied,
            //including whatever lmpOwningAgency value they may have spoofed. The
            //server-stored copy (CurrentVessels + disk) was server-authoritatively scrubbed
            //by RawConfigNodeInsertOrUpdate; the relayed bytes were NOT. Stage 5.18a client
            //mirrors MUST treat the relayed lmpOwningAgency as advisory and re-derive
            //ownership from VesselSync replies (which serialise from the server's
            //authoritative store via GetVesselInConfigNodeFormat) or from
            //AgencyVisibilityMsgData (Stage 5.18c). The server-side LockSystem rejection
            //in Stage 5.17a reads from the authoritative store, so cross-agency lock
            //decisions are safe regardless of relay content.
            MessageQueuer.RelayMessage<VesselSrvMsg>(client, msgData);
        }

        private static void HandleVesselsSync(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselSyncMsgData)message;

            //[Stage 5.18d slice (i)] Force-full-sync on the first VesselSync per
            //connection under gate=on. Closes the 5.18b reconnect gap where KSP's
            //FlightGlobals.Vessels retains prior-connection vessels but their
            //lmpOwningAgency stamp was stripped by BackupVessel — the legacy
            //incremental diff below would skip them ("client already has these"),
            //leaving the client-side AgencySystem.VesselOwnership registry empty
            //for known vessels. Full-sync routes every vessel through
            //GetVesselInConfigNodeFormat which serialises the canonical stamp.
            var forceFullSync = AgencyVesselSyncPolicy.ShouldFullSync(
                AgencySystem.PerAgencyEnabled, client.HasReceivedInitialVesselsSync);

            var allVessels = VesselStoreSystem.CurrentVessels.Keys.ToList();

            if (!forceFullSync)
            {
                //Legacy diff path: only ship vessels the client claims it doesn't have.
                for (var i = 0; i < msgData.VesselsCount; i++)
                    allVessels.Remove(msgData.VesselIds[i]);
            }
            else
            {
                LunaLog.Normal(
                    $"[fix:per-agency-career] Forcing full vessel sync for {client.PlayerName} on first sync " +
                    $"this connection ({allVessels.Count} vessels) — repopulates client-side VesselOwnership " +
                    "stamps after the reconnect's OnDisabled clear.");
            }

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

            //[Stage 5.18d slice (i) — server-systems-review + upgrade-lens v1]
            //Flip the per-connection "has-synced-once" flag ONLY when the full-
            //sync branch actually executed. Under gate=off the flag stays false,
            //so an admin who runs /changesettings PerAgencyCareer=true mid-
            //session gets a full-sync on the next sync from each currently-
            //connected client — the moment the gate flips on, the next sync
            //repopulates that client's registry without requiring a kick +
            //reconnect. The earlier "set unconditionally" logic had the
            //inverted semantics: every gate-off sync set the flag true, so a
            //subsequent gate-on toggle silently took the diff path and the
            //client's registry stayed empty until reconnect.
            if (forceFullSync)
                client.HasReceivedInitialVesselsSync = true;

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

            //[Stage 5.17a write-path counterpart, server-systems review session 19] Cross-agency
            //rejection on the dominant vessel. Couple is at least as destructive as Remove +
            //Position combined: it rewrites the dominant's AuthoritativeSubspaceId, removes the
            //weak vessel, and broadcasts a remove to every peer. Without this guard a cross-
            //agency Bob could take over Alice's dominant vessel as the merged target. The
            //weak-vessel side intentionally is NOT separately guarded here — if Bob owns the
            //weak vessel and Alice owns the dominant, the dominant-side guard already refuses;
            //if both are foreign to Bob, same.
            if (RejectIfCrossAgencyWrite(client.PlayerName, msgData)) return;

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

            //[Mod-compat S1] Reconcile lmpOwningAgency on the surviving vessel. The kept vessel
            //keeps its stamp by default; the one mutation is when kept was Unassigned (pre-0.31
            //sentinel) and merged was tracked — in which case adopt the merged stamp to preserve
            //agency continuity. Cross-agency couples (both stamps non-Empty, differ) emit a
            //Warning for operator visibility. Covers stock docking + KAS pipe coupling identically
            //(both ride Part.Couple). No-op under gate=off. See AgencyVesselCoupleReconciler.cs
            //for the full rule table.
            AgencyVesselCoupleReconciler.Reconcile(msgData.VesselId, msgData.CoupledVesselId);

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
