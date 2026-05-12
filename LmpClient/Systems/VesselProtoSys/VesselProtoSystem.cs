using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Extensions;
using LmpClient.Systems.KerbalSys;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.Utilities;
using LmpClient.VesselUtilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LmpClient.Systems.VesselProtoSys
{
    /// <summary>
    /// This system handles the vessel loading into the game and sending our vessel structure to other players.
    /// </summary>
    public class VesselProtoSystem : MessageSystem<VesselProtoSystem, VesselProtoMessageSender, VesselProtoMessageHandler>
    {
        #region Fields & properties

        private static readonly HashSet<Guid> QueuedVesselsToSend = new HashSet<Guid>();

        public readonly HashSet<Guid> VesselsUnableToLoad = new HashSet<Guid>();

        public ConcurrentDictionary<Guid, VesselProtoQueue> VesselProtos { get; } = new ConcurrentDictionary<Guid, VesselProtoQueue>();

        //Per-vessel record of "the value of vessel.parts.Count the last time we
        //broadcast a Part-count-drift message for this vessel id". Gates
        //SendVesselDefinition's drift check so we never re-broadcast the same drift
        //state more than once. Background: SendVesselDefinition fires every 2.5 s and
        //compares vessel.parts.Count against vessel.protoVessel.protoPartSnapshots.Count.
        //SendVesselMessage rewrites vessel.protoVessel via vessel.BackupVessel() on
        //every send, BUT stock KSP's BackupVessel can legitimately produce a snapshot
        //count that differs from the live parts.Count for several reasons (dynamic
        //parts, EVA-suit attachment, robotic / breaking-parts states that don't
        //round-trip 1:1 through serialisation). When that happens the drift check
        //fires on every 2.5 s tick forever, broadcasting an identical proto and
        //forcing every receiving client to pay a destructive ProtoVessel.Load on each
        //arrival -- exactly the ~3 s cadence of full reloads observed in KSP.log when
        //multiple peer vessels are present. Caching the count we last broadcast lets
        //us short-circuit when the drift is "stable" (server has the latest data;
        //nothing new to send) while still firing immediately when parts.Count moves
        //to a value we have not yet sent (genuine structural change on the
        //originating side).
        private static readonly ConcurrentDictionary<Guid, int> LastBroadcastDriftPartCount =
            new ConcurrentDictionary<Guid, int>();

        public bool ProtoSystemReady => Enabled && FlightGlobals.ready && HighLogic.LoadedScene == GameScenes.FLIGHT &&
            FlightGlobals.ActiveVessel != null && !VesselCommon.IsSpectating;

        public VesselProtoEvents VesselProtoEvents { get; } = new VesselProtoEvents();

        public VesselRemoveSystem VesselRemoveSystem => VesselRemoveSystem.Singleton;

        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(VesselProtoSystem);

        protected override bool ProcessMessagesInUnityThread => false;

        protected override void OnEnabled()
        {
            base.OnEnabled();

            GameEvents.onFlightReady.Add(VesselProtoEvents.FlightReady);
            GameEvents.onGameSceneLoadRequested.Add(VesselProtoEvents.OnSceneRequested);

            GameEvents.OnTriggeredDataTransmission.Add(VesselProtoEvents.TriggeredDataTransmission);
            GameEvents.OnExperimentStored.Add(VesselProtoEvents.ExperimentStored);
            ExperimentEvent.onExperimentReset.Add(VesselProtoEvents.ExperimentReset);

            PartEvent.onPartDecoupled.Add(VesselProtoEvents.PartDecoupled);
            PartEvent.onPartUndocked.Add(VesselProtoEvents.PartUndocked);
            PartEvent.onPartCoupled.Add(VesselProtoEvents.PartCoupled);

            WarpEvent.onTimeWarpStopped.Add(VesselProtoEvents.WarpStopped);

            SetupRoutine(new RoutineDefinition(0, RoutineExecution.Update, CheckVesselsToLoad));
            SetupRoutine(new RoutineDefinition(2500, RoutineExecution.Update, SendVesselDefinition));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();

            GameEvents.onFlightReady.Remove(VesselProtoEvents.FlightReady);
            GameEvents.onGameSceneLoadRequested.Remove(VesselProtoEvents.OnSceneRequested);

            GameEvents.OnTriggeredDataTransmission.Remove(VesselProtoEvents.TriggeredDataTransmission);
            GameEvents.OnExperimentStored.Remove(VesselProtoEvents.ExperimentStored);
            ExperimentEvent.onExperimentReset.Remove(VesselProtoEvents.ExperimentReset);

            PartEvent.onPartDecoupled.Remove(VesselProtoEvents.PartDecoupled);
            PartEvent.onPartUndocked.Remove(VesselProtoEvents.PartUndocked);
            PartEvent.onPartCoupled.Remove(VesselProtoEvents.PartCoupled);

            WarpEvent.onTimeWarpStopped.Remove(VesselProtoEvents.WarpStopped);

            //This is the main system that handles the vesselstore so if it's disabled clear the store too
            VesselProtos.Clear();
            VesselsUnableToLoad.Clear();
            QueuedVesselsToSend.Clear();
            LastBroadcastDriftPartCount.Clear();
            LocalTopologyTracker.ClearAll();
        }

        #endregion

        #region Update routines

        /// <summary>
        /// Send the definition of our own vessel and the secondary vessels.
        /// </summary>
        private void SendVesselDefinition()
        {
            try
            {
                if (!ProtoSystemReady) return;

                if (ShouldBroadcastDriftFor(FlightGlobals.ActiveVessel))
                    MessageSender.SendVesselMessage(FlightGlobals.ActiveVessel, reason: "Part count drift (active vessel)");

                foreach (var vessel in VesselCommon.GetSecondaryVessels())
                {
                    if (ShouldBroadcastDriftFor(vessel))
                        MessageSender.SendVesselMessage(vessel, reason: "Part count drift (secondary vessel)");
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error in SendVesselDefinition {e}");
            }

        }

        /// <summary>
        /// Returns true when <paramref name="vessel"/> has a part-count drift we have not
        /// already broadcast. Combines two short-circuits:
        /// 1. <c>parts.Count == protoVessel.protoPartSnapshots.Count</c> means the live
        ///    Vessel and its stored proto agree -- no drift, nothing to send.
        /// 2. <c>parts.Count == LastBroadcastDriftPartCount[vesselId]</c> means we already
        ///    broadcast at this exact part count; the server has the latest data and
        ///    re-sending would just trigger an identical receiving-side reload storm.
        /// The cache entry is updated when (and only when) we decide a broadcast is
        /// warranted, so any subsequent change to <c>parts.Count</c> on the originating
        /// side immediately re-arms the check.
        /// </summary>
        private static bool ShouldBroadcastDriftFor(Vessel vessel)
        {
            if (vessel == null || vessel.protoVessel?.protoPartSnapshots == null) return false;

            var liveCount = vessel.parts.Count;
            var protoCount = vessel.protoVessel.protoPartSnapshots.Count;
            if (liveCount == protoCount) return false;

            //ConcurrentDictionary even though VesselProtoSystem is single-threaded for
            //the send path: we also clear entries from RemoveVessel which can be
            //invoked from message-handling threads. The cost difference vs Dictionary
            //is irrelevant at 2.5 s tick granularity.
            if (LastBroadcastDriftPartCount.TryGetValue(vessel.id, out var lastSent) && lastSent == liveCount)
                return false;

            LastBroadcastDriftPartCount[vessel.id] = liveCount;
            return true;
        }

        /// <summary>
        /// Check vessels that must be loaded
        /// </summary>
        public void CheckVesselsToLoad()
        {
            if (HighLogic.LoadedScene < GameScenes.SPACECENTER) return;

            try
            {
                // Drain any KerbalProto messages queued since the last KerbalSystem.LoadKerbals
                // tick BEFORE running vesselProto.Load(...) on any vessel this tick. Background:
                // KerbalSystem.LoadKerbals runs on its own ~1 s routine and consumes
                // KerbalsToProcess only during that tick; CheckVesselsToLoad runs on a faster
                // routine, so on a busy session (or during the burst that immediately follows
                // initial sync, when KerbalProto messages can still be in flight as VesselProto
                // messages start arriving) we can land here with named-but-unmerged kerbals
                // sitting in KerbalsToProcess. Stock KSP's ProtoPartSnapshot ConfigNode ctor
                // resolves "crew = NAME" against HighLogic.CurrentGame.CrewRoster *as it stands
                // right now*, so any kerbal that's queued-but-not-merged still produces a null
                // entry in protoModuleCrew + the "[Protocrewmember]: Instance of crewmember  in
                // part X on Y does not exist in the roster" warning, exactly as if the kerbal
                // had never been sent. Co-sending crew with each VesselProto already eliminates
                // the originating-side gap; this drain eliminates the receiving-side timing
                // gap so the two together close the loop. Cheap on the steady state -- the
                // queue is normally empty, this is one TryDequeue/Count check.
                if (KerbalSystem.Singleton != null && !KerbalSystem.Singleton.KerbalsToProcess.IsEmpty)
                {
                    KerbalSystem.Singleton.LoadKerbalsIntoGame();
                }

                foreach (var keyVal in VesselProtos)
                {
                    if (keyVal.Value.TryPeek(out var vesselProto) && vesselProto.GameTime <= TimeSyncSystem.UniversalTime)
                    {
                        //Topology-mutation quarantine. If we just locally rewrote this
                        //vessel's part tree via Couple / Decouple / Undock, refuse to
                        //apply incoming server protos for it until the cascade has
                        //settled (~250 ms after the last local mutation, with each
                        //new mutation re-arming the clock). Leaving the queue head
                        //peeked-but-not-dequeued retries it on the next Update tick
                        //in FIFO order so there is no risk of starvation.
                        if (LocalTopologyTracker.IsQuarantined(keyVal.Key, out _))
                            continue;

                        keyVal.Value.TryDequeue(out _);

                        if (VesselRemoveSystem.VesselWillBeKilled(vesselProto.VesselId))
                            continue;

                        var forceReload = vesselProto.ForceReload;
                        var protoVessel = vesselProto.CreateProtoVessel();
                        keyVal.Value.Recycle(vesselProto);

                        var verboseErrors = !VesselsUnableToLoad.Contains(vesselProto.VesselId);
                        if (protoVessel == null || !protoVessel.Validate(verboseErrors) || protoVessel.HasInvalidParts(verboseErrors))
                        {
                            VesselsUnableToLoad.Add(vesselProto.VesselId);
                            continue;
                        }

                        VesselsUnableToLoad.Remove(vesselProto.VesselId);

                        var existingVessel = FlightGlobals.FindVessel(vesselProto.VesselId);
                        if (existingVessel == null)
                        {
                            if (VesselLoader.LoadVessel(protoVessel, forceReload))
                            {
                                LunaLog.Log($"[LMP]: Vessel {protoVessel.vesselID} loaded");
                                VesselLoadEvent.onLmpVesselLoaded.Fire(protoVessel.vesselRef);
                            }
                        }
                        else
                        {
                            if (VesselLoader.LoadVessel(protoVessel, forceReload))
                            {
                                LunaLog.Log($"[LMP]: Vessel {protoVessel.vesselID} reloaded");
                                VesselReloadEvent.onLmpVesselReloaded.Fire(protoVessel.vesselRef);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error in CheckVesselsToLoad {e}");
            }
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Sends a delayed vessel definition to the server.
        /// Call this method if you expect to do a lot of modifications to a vessel and you want to send it only once.
        /// <paramref name="reason"/> is forwarded to the server's craft create/remove audit log.
        /// </summary>
        public void DelayedSendVesselMessage(Guid vesselId, float delayInSec, bool forceReload = false, string reason = null)
        {
            if (QueuedVesselsToSend.Contains(vesselId)) return;

            QueuedVesselsToSend.Add(vesselId);
            CoroutineUtil.StartDelayedRoutine("QueueVesselMessageAsPartsChanged", () =>
            {
                QueuedVesselsToSend.Remove(vesselId);

                LunaLog.Log($"[LMP]: Sending delayed proto vessel {vesselId}");
                MessageSender.SendVesselMessage(FlightGlobals.FindVessel(vesselId), forceReload, reason);
            }, delayInSec);
        }

        /// <summary>
        /// Removes a vessel from the system
        /// </summary>
        public void RemoveVessel(Guid vesselId)
        {
            VesselProtos.TryRemove(vesselId, out _);
            //Drop the drift cache entry so a vessel re-created with the same id later
            //in the session (e.g. revert-to-launch from EDITOR, or a re-spawn after a
            //remote kill+resend) starts from a clean slate and the first legitimate
            //broadcast after the recreate isn't suppressed.
            LastBroadcastDriftPartCount.TryRemove(vesselId, out _);
            //A vessel id resurrected in the same session must not inherit a stale
            //"I just mutated locally" record from the previous incarnation, which
            //would suppress the first wire update on the new vessel.
            LocalTopologyTracker.ClearVessel(vesselId);
        }

        #endregion
    }
}
