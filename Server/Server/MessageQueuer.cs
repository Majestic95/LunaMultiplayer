using LmpCommon.Enums;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Linq;

namespace Server.Server
{
    public class MessageQueuer
    {
        /// <summary>
        /// [perf:relay-scene Phase 1] Sends a message to all the clients except the
        /// one given as parameter, FILTERED on recipient's current KSP scene.
        /// Recipients whose scene cannot render continuous vessel-state (i.e. not
        /// Flight or TrackingStation) are skipped.
        ///
        /// Pre-Phase-1 clients (Scene=Unknown — they don't ship the tail-byte) are
        /// treated as "relay always" to preserve the baseline broadcast behaviour;
        /// see <see cref="ShouldReceiveVesselUpdate"/>.
        ///
        /// When <c>OptimizationSettings.SceneAwareRelayEnabled</c>=false, falls back
        /// to <see cref="RelayMessage{T}"/> (operator escape hatch — restores the
        /// pre-Phase-1 baseline without a binary downgrade).
        ///
        /// Intended call sites: continuous vessel-STATE updates only —
        /// <c>VesselMsgReader</c> cases for Position / Flightstate / Update /
        /// Resource / PartSync{Field,UiField,Call} / ActionGroup / Fairing
        /// (9 sites). Structural / registry-mutating relays (Proto / Sync /
        /// Couple / Remove / **Decouple / Undock**) MUST stay on
        /// <see cref="RelayMessage{T}"/> — recipients need them in every scene
        /// so that scene entry into Flight finds a populated
        /// <c>FlightGlobals.Vessels</c>. Don't extend this to Decouple/Undock
        /// without breaking docking-handoff sync for non-Flight recipients.
        /// </summary>
        public static void RelayMessageToFlightScene<T>(ClientStructure exceptClient, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            if (!OptimizationSettings.SettingsStore.SceneAwareRelayEnabled)
            {
                RelayMessage<T>(exceptClient, data);
                return;
            }

            foreach (var otherClient in ServerContext.Clients.Values.Where(c => !Equals(c, exceptClient) && ShouldReceiveVesselUpdate(c)))
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// ClientStructure wrapper for <see cref="ShouldRelayToScene"/>. Reads the
        /// recipient's last-reported scene off <c>PlayerStatus.Scene</c> (set by
        /// <see cref="Server.Message.PlayerStatusMsgReader.HandleMessage"/> on every
        /// inbound PlayerStatusSet) and delegates the gate decision.
        ///
        /// Caller already gates on <c>OptimizationSettings.SceneAwareRelayEnabled</c>
        /// so this helper doesn't re-check it.
        /// </summary>
        internal static bool ShouldReceiveVesselUpdate(ClientStructure recipient)
        {
            var scene = recipient?.PlayerStatus?.Scene ?? ClientSceneType.Unknown;
            return ShouldRelayToScene(scene);
        }

        /// <summary>
        /// Pure helper (Phase 1 — server-side-offload). True iff a recipient
        /// reporting <paramref name="scene"/> should receive a continuous vessel-
        /// state relay (Position / Flightstate / Update / Resource / PartSync* /
        /// ActionGroup / Fairing).
        ///
        /// Three branches:
        ///   - <see cref="ClientSceneType.Unknown"/> → true (pre-Phase-1 client or
        ///     post-connect pre-first-SetScene window — relay always for compat).
        ///   - <see cref="ClientSceneType.Flight"/> / <see cref="ClientSceneType.TrackingStation"/>
        ///     → true (recipient will actually render the relayed state).
        ///   - Any other scene (MainMenu / SpaceCenter / Editor / RD / Mission /
        ///     Other) → false. None of these render remote vessel state, so the
        ///     recipient would discard the message after decode.
        ///
        /// Public for ServerTest direct invocation (mirrors the
        /// <c>WarpSystem.DetectSoloTransitions</c> pure-helper pattern from
        /// the per-agency workstream — extract decision math to a no-side-
        /// effects signature so unit tests don't construct full ClientStructures
        /// + NetConnections to exercise it).
        /// </summary>
        public static bool ShouldRelayToScene(ClientSceneType scene)
        {
            //Compat: pre-Phase-1 client (didn't write the Scene byte → server
            //deserialised Unknown) OR post-connect pre-first-SetScene window
            //(server hasn't received the first PlayerStatus.Set yet). Both
            //resolve to "relay always" — gives the recipient at least one tick
            //of full state before any potential filtering kicks in.
            if (scene == ClientSceneType.Unknown) return true;
            //SpaceCenter excluded — KSC's in-scene active-vessel marker is for the
            //LOCAL active vessel on a separate code path, not a relay-driven
            //rendering of remote vessels (spec §3.d Q2). The full list of
            //non-relay scenes is MainMenu / SpaceCenter / Editor / RD / Mission /
            //Other; all of them discard continuous vessel state after decode.
            return scene == ClientSceneType.Flight || scene == ClientSceneType.TrackingStation;
        }

        /// <summary>
        /// [perf:relay-body Phase 2] Composed Phase 1 + Phase 2 filter for continuous
        /// vessel-state relays. Drops the message to a recipient when EITHER scene
        /// filter rejects (recipient not in Flight/TrackingStation) OR body filter
        /// rejects (sender's vessel orbiting a different celestial body than the
        /// recipient's active vessel).
        ///
        /// Either gate can be operator-disabled independently via OptimizationSettings.
        /// When both are off the call is byte-equivalent to <see cref="RelayMessage{T}"/>.
        ///
        /// Use this at the same 9 continuous-state sites that Phase 1 wired —
        /// Position / Flightstate / Update / Resource / PartSync{Field,UiField,Call} /
        /// ActionGroup / Fairing. Structural relays (Proto / Sync / Couple / Remove /
        /// Decouple / Undock) stay on <see cref="RelayMessage{T}"/> for the same
        /// reason as Phase 1: recipients need them in every scene + at every body to
        /// keep <c>FlightGlobals.Vessels</c> consistent for the eventual scene/body
        /// entry that lets them see the vessel.
        /// </summary>
        public static void RelayMessageToFlightSceneSameBody<T>(ClientStructure exceptClient, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            var sceneEnabled = OptimizationSettings.SettingsStore.SceneAwareRelayEnabled;
            var bodyEnabled = OptimizationSettings.SettingsStore.SameBodyFilterEnabled;

            if (!sceneEnabled && !bodyEnabled)
            {
                //Both gates off → exact pre-Phase-1 broadcast.
                RelayMessage<T>(exceptClient, data);
                return;
            }

            //Resolve sender's vessel body once outside the loop (constant across
            //recipients for a given message). Cheap for Position (BodyName is in
            //the msg); single ConcurrentDictionary lookup for other types.
            var senderBody = bodyEnabled ? ResolveSenderBody(data) : null;

            foreach (var otherClient in ServerContext.Clients.Values)
            {
                if (Equals(otherClient, exceptClient)) continue;
                if (sceneEnabled && !ShouldReceiveVesselUpdate(otherClient)) continue;
                if (bodyEnabled && !ShouldRelayToBody(senderBody, otherClient.ActiveVesselBodyName)) continue;
                SendToClient(otherClient, GenerateMessage<T>(data));
            }
        }

        /// <summary>
        /// Pure helper (Phase 2). True iff a vessel orbiting <paramref name="vesselBody"/>
        /// should be relayed to a recipient whose active vessel orbits
        /// <paramref name="recipientBody"/>.
        ///
        /// Three branches:
        ///   - <paramref name="vesselBody"/> null/empty → true (sender hasn't reported
        ///     body yet, or message type doesn't carry one and server-side lookup
        ///     missed — permissive).
        ///   - <paramref name="recipientBody"/> null/empty → true (recipient hasn't
        ///     sent first Flightstate or first Position-for-active-vessel — permissive
        ///     for the join window).
        ///   - Both non-empty → exact <see cref="StringComparison.Ordinal"/> match.
        ///     Different bodies = drop.
        ///
        /// Same-body-only by design — Mun is NOT considered "same body" as Kerbin
        /// even though Mun orbits in Kerbin's SOI. Avoiding the SOI graph keeps the
        /// filter robust against modded planet packs (RSS / OPM / GPP / etc.).
        ///
        /// Public for ServerTest direct invocation.
        /// </summary>
        public static bool ShouldRelayToBody(string vesselBody, string recipientBody)
        {
            if (string.IsNullOrEmpty(vesselBody)) return true;
            if (string.IsNullOrEmpty(recipientBody)) return true;
            return string.Equals(vesselBody, recipientBody, StringComparison.Ordinal);
        }

        /// <summary>
        /// Phase 2 helper. Resolves the body that <paramref name="data"/>'s subject
        /// vessel is orbiting.
        ///
        /// VesselPositionMsgData carries BodyName on the wire (line 14 of
        /// LmpCommon/Message/Data/Vessel/VesselPositionMsgData.cs) — use it directly
        /// for the cheap synchronous case (Position is the dominant volume).
        ///
        /// Other vessel-state messages don't carry the body. Fall back to a
        /// VesselStoreSystem lookup — the body is round-tripped onto Vessel.Orbit.IDENT
        /// by VesselDataUpdater.WritePositionDataToFile every 2.5s, so the lookup is
        /// eventually-consistent. A vessel that just transitioned SOI may be ~2.5s
        /// stale relative to the latest Position, but Phase 2's worst case is "one
        /// extra tick of cross-body relay" which is the same cost as the pre-Phase-2
        /// baseline.
        ///
        /// Internal — only the composed filter needs to call it.
        /// </summary>
        /// <summary>
        /// [perf:relay-cadence Phase 3] Position-only relay path. Composes Phase 1 +
        /// Phase 2 + Phase 3 (per-vessel cadence by lock holder). Throttles relays for
        /// vessels that no client holds Control lock on — debris, abandoned satellites,
        /// stranded probes — to one relay per (<c>SecondaryVesselUpdatesMsInterval</c> ×
        /// <c>UnpilotedVesselCadenceMultiplier</c>) ms instead of the baseline 50ms.
        /// Vessels with an active Control lock relay at full cadence (unchanged).
        ///
        /// Position-specific (not extended to Flightstate / Update / Resource / etc.)
        /// because: (a) Position is the dominant volume (50ms cadence vs 1500ms / 5000ms
        /// for the others); (b) Flightstate by design is only sent for the local active
        /// vessel which by definition has Control lock; (c) Update / Resource carry
        /// game-relevant state that shouldn't be throttled below their own cadence.
        ///
        /// Per-vessel state — <c>vessel.LastRelayedPositionMs</c> updated on each
        /// throttle-passing relay decision. Skipping a relay does NOT update the
        /// timestamp, so the next inbound after the throttle window passes
        /// unconditionally.
        ///
        /// Lock acquired mid-stream: <see cref="Server.System.LockSystem.LockQuery.ControlLockExists"/>
        /// returns true → throttle bypassed → next message relays immediately + stamps
        /// timestamp. Lock released mid-stream: subsequent messages enter throttle path;
        /// the gap to the previous (full-cadence) relay was &lt;= 50ms (well under any
        /// reasonable throttle window) so the first post-release inbound still relays,
        /// then subsequent ones throttle.
        /// </summary>
        public static void RelayPositionMessage<T>(ClientStructure exceptClient, VesselPositionMsgData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            var multiplier = OptimizationSettings.SettingsStore.UnpilotedVesselCadenceMultiplier;
            //Cadence-gate the whole relay decision. Skips before the per-recipient loop
            //so an unpiloted vessel saves the loop iteration cost too (cheaper than
            //per-recipient throttle).
            if (multiplier > 1
                && VesselStoreSystem.CurrentVessels.TryGetValue(data.VesselId, out var vessel))
            {
                var nowMs = ServerContext.ServerClock.ElapsedMilliseconds;
                var secondaryIntervalMs = IntervalSettings.SettingsStore.SecondaryVesselUpdatesMsInterval;
                if (!ShouldRelayPositionByCadence(data.VesselId, vessel.LastRelayedPositionMs, nowMs, secondaryIntervalMs, multiplier))
                    return;
                vessel.LastRelayedPositionMs = nowMs;
            }

            //Fall through to the composed Phase 1 + Phase 2 filter for the actual fan-out.
            RelayMessageToFlightSceneSameBody<T>(exceptClient, data);
        }

        /// <summary>
        /// Pure helper (Phase 3). True iff a Position message for
        /// <paramref name="vesselId"/> should relay given the per-vessel cadence
        /// throttle. Public for ServerTest direct invocation.
        ///
        /// Three branches:
        ///   - <paramref name="multiplier"/> &lt;= 1 → true (throttle off / no-op).
        ///   - <see cref="Server.System.LockSystem.LockQuery.ControlLockExists"/>(vesselId) →
        ///     true (someone is actively piloting this vessel — full cadence).
        ///   - Else: relay only if (<paramref name="nowMs"/> - <paramref name="lastRelayedMs"/>)
        ///     &gt;= (<paramref name="secondaryIntervalMs"/> × <paramref name="multiplier"/>).
        ///
        /// Pure on the inputs except for the LockSystem read — that's an implicit
        /// dependency the test harness needs to set up (or mock). For the unit-test
        /// surface in ServerTest/CadenceThrottleTest.cs, we exercise the throttle
        /// math via the multiplier &lt;= 1 / vessel-lock-absent branches; the
        /// lock-present positive branch is covered indirectly by the existing
        /// LockSystemTest baseline.
        /// </summary>
        public static bool ShouldRelayPositionByCadence(Guid vesselId, long lastRelayedMs, long nowMs, int secondaryIntervalMs, int multiplier)
        {
            if (multiplier <= 1) return true;
            if (LockSystem.LockQuery.ControlLockExists(vesselId)) return true;
            var minIntervalMs = (long)secondaryIntervalMs * multiplier;
            return (nowMs - lastRelayedMs) >= minIntervalMs;
        }

        internal static string ResolveSenderBody(IMessageData data)
        {
            //Position carries body inline — cheapest path, no store lookup.
            if (data is VesselPositionMsgData posMsg) return posMsg.BodyName;
            //Non-Position relays: read the atomic CurrentBodyName cache on Vessel
            //(set by VesselDataUpdater's proto ingest + WritePositionDataToFile,
            //both under the per-vessel semaphore). LOCK-FREE here because the cache
            //is a simple string field — assignment is atomic per ECMA-335 §I.12.6.6,
            //so the reader sees either the previous or the new reference, never torn.
            //Pre-Phase-2 implementation called Vessel.GetOrbitingBodyName() which
            //enumerated the Orbit MixedCollection on the receive thread — racy with
            //the background WritePositionDataToFile mutating the same collection.
            //Phase 2 M1 review fix.
            if (data is VesselBaseMsgData vesselMsg
                && VesselStoreSystem.CurrentVessels.TryGetValue(vesselMsg.VesselId, out var vessel))
            {
                return vessel.CurrentBodyName;
            }
            return null;
        }

        /// <summary>
        /// Sends a message to all the clients except the one given as parameter that are in the same subspace
        /// </summary>
        public static void RelayMessageToSubspace<T>(ClientStructure exceptClient, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            RelayMessageToSubspace<T>(exceptClient, data, exceptClient.Subspace);
        }

        /// <summary>
        /// Sends a message to all the clients in the given subspace
        /// </summary>
        public static void SendMessageToSubspace<T>(IMessageData data, int subspace) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values.Where(c => c.Subspace == subspace))
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to all the clients except the one given as parameter that are in the subspace given as parameter
        /// </summary>
        public static void RelayMessageToSubspace<T>(ClientStructure exceptClient, IMessageData data, int subspace) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values.Where(c => !Equals(c, exceptClient) && c.Subspace == subspace))
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to all the clients except the one given as parameter
        /// </summary>
        public static void RelayMessage<T>(ClientStructure exceptClient, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values.Where(c => !Equals(c, exceptClient)))
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to all the clients
        /// </summary>
        public static void SendToAllClients<T>(IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            foreach (var otherClient in ServerContext.Clients.Values)
                SendToClient(otherClient, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Sends a message to the given client
        /// </summary>
        public static void SendToClient<T>(ClientStructure client, IMessageData data) where T : class, IServerMessageBase
        {
            if (data == null) return;

            SendToClient(client, GenerateMessage<T>(data));
        }

        /// <summary>
        /// Disconnects the given client
        /// </summary>
        public static void SendConnectionEnd(ClientStructure client, string reason)
        {
            ClientConnectionHandler.DisconnectClient(client, reason);
        }

        /// <summary>
        /// Disconnect all clients
        /// </summary>
        public static void SendConnectionEndToAll(string reason)
        {
            foreach (var client in ClientRetriever.GetAuthenticatedClients())
                SendConnectionEnd(client, reason);
        }

        #region Private

        private static void SendToClient(ClientStructure client, IServerMessageBase msg)
        {
            if (msg?.Data == null) return;

            client?.SendMessageQueue.Enqueue(msg);
        }

        private static T GenerateMessage<T>(IMessageData data) where T : class, IServerMessageBase
        {
            var newMessage = ServerContext.ServerMessageFactory.CreateNew<T>(data);
            return newMessage;
        }

        #endregion
    }
}