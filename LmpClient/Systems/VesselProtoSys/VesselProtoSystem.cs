using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Extensions;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
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

        /// <summary>
        /// Tracks last-sent maneuver node signatures per vessel to detect dV changes between periodic ticks.
        /// Key: vessel ID. Value: concatenated UT|dV string for all nodes, or empty if no nodes.
        /// </summary>
        private static readonly Dictionary<Guid, string> ManeuverSignatures = new Dictionary<Guid, string>();

        /// <summary>
        /// Maximum number of expensive ProtoVessel.Load calls (fresh load or destructive
        /// reload) executed in a single CheckVesselsToLoad tick. The drain loop previously
        /// processed every queue whose head was ready in the same frame, so a burst of N
        /// peer-side broadcasts produced an N x ProtoVessel.Load spike — visible as
        /// 200-1000 ms of unaccounted single-frame work when three or more vessels needed
        /// reloading simultaneously. Capping the per-frame budget spreads the same total
        /// work across multiple frames; queues whose heads we skip stay peeked (not
        /// dequeued) so they are retried next tick in FIFO order with no starvation.
        /// Cheap proto-swap operations in SPACECENTER / EDITOR do NOT count against this
        /// budget — only stock-KSP ProtoVessel.Load calls do, because they are the actual
        /// cost driver. 2 is conservative: enough headroom to catch up after a brief
        /// network burst without letting any single frame eat two full destructive
        /// reloads back-to-back from a dead start.
        /// Ported from upstream Release/0_29_2 commit 346ef48a (Drew Banyai).
        /// </summary>
        private const int MaxExpensiveReloadsPerTick = 2;

        public readonly HashSet<Guid> VesselsUnableToLoad = new HashSet<Guid>();

        public ConcurrentDictionary<Guid, VesselProtoQueue> VesselProtos { get; } = new ConcurrentDictionary<Guid, VesselProtoQueue>();

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

            GameEvents.onManeuverAdded.Add(VesselProtoEvents.ManeuverNodeAdded);
            GameEvents.onManeuverRemoved.Add(VesselProtoEvents.ManeuverNodeRemoved);

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

            GameEvents.onManeuverAdded.Remove(VesselProtoEvents.ManeuverNodeAdded);
            GameEvents.onManeuverRemoved.Remove(VesselProtoEvents.ManeuverNodeRemoved);

            //This is the main system that handles the vesselstore so if it's disabled clear the store too
            VesselProtos.Clear();
            VesselsUnableToLoad.Clear();
            QueuedVesselsToSend.Clear();
            ManeuverSignatures.Clear();
        }

        #endregion

        #region Update routines

        /// <summary>
        /// Send the definition of our own vessel and the secondary vessels.
        /// Also detects maneuver node dV changes (which fire no KSP event) and re-sends when they differ.
        /// </summary>
        private void SendVesselDefinition()
        {
            try
            {
                if (ProtoSystemReady)
                {
                    var activeVessel = FlightGlobals.ActiveVessel;

                    if (activeVessel.parts.Count != activeVessel.protoVessel.protoPartSnapshots.Count)
                        MessageSender.SendVesselMessage(activeVessel);

                    CheckAndSendManeuverChanges(activeVessel);

                    foreach (var vessel in VesselCommon.GetSecondaryVessels())
                    {
                        if (vessel.parts.Count != vessel.protoVessel.protoPartSnapshots.Count)
                            MessageSender.SendVesselMessage(vessel);
                    }
                }
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error in SendVesselDefinition {e}");
            }
        }

        /// <summary>
        /// Compares the current maneuver node state of a vessel against the last-sent snapshot.
        /// Sends the vessel proto if anything has changed (node added, removed, or dV edited).
        /// Only acts when we hold the update lock for this vessel.
        /// </summary>
        private void CheckAndSendManeuverChanges(Vessel vessel)
        {
            if (vessel == null) return;
            if (!LockSystem.LockQuery.UpdateLockBelongsToPlayer(vessel.id, SettingsSystem.CurrentSettings.PlayerName)) return;

            var sig = GetManeuverSignature(vessel);
            if (ManeuverSignatures.TryGetValue(vessel.id, out var lastSig))
            {
                if (lastSig != sig)
                {
                    ManeuverSignatures[vessel.id] = sig;
                    LunaLog.Log($"[LMP]: Maneuver nodes changed on {vessel.vesselName}, sending updated proto");
                    MessageSender.SendVesselMessage(vessel);
                }
            }
            else
            {
                // First poll for this vessel — record baseline without sending
                ManeuverSignatures[vessel.id] = sig;
            }
        }

        /// <summary>
        /// Produces a compact string signature of all maneuver nodes on a vessel.
        /// Format: "UT|dVx,dVy,dVz;UT|dVx,dVy,dVz;...". Empty string if no nodes.
        /// </summary>
        private static string GetManeuverSignature(Vessel vessel)
        {
            var nodes = vessel?.patchedConicSolver?.maneuverNodes;
            if (nodes == null || nodes.Count == 0) return string.Empty;

            var parts = new string[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                var dv = nodes[i].DeltaV;
                parts[i] = $"{nodes[i].UT:F1}|{dv.x:F4},{dv.y:F4},{dv.z:F4}";
            }
            return string.Join(";", parts);
        }

        /// <summary>
        /// Returns true for scenes where peer vessels exist in FlightGlobals strictly as
        /// data carriers (unloaded / packed) and the player cannot see or interact with
        /// them in-world. In those scenes a wire-side structural update can be applied
        /// with a cheap proto-swap (<see cref="VesselLoader.UpdateProtoInPlace"/>) instead
        /// of the full destructive <see cref="VesselLoader.LoadVessel"/> path; the latter
        /// would still create a brand-new <see cref="Vessel"/> <see cref="UnityEngine.GameObject"/>,
        /// run every <c>VesselModule.Awake</c>, and pay stock KSP's per-part persistentId
        /// collision walk for no visible benefit while the player is in the VAB / SPH or
        /// the Space Center scene. In FLIGHT (the in-world vessel may be loaded and
        /// rendered) and TRACKSTATION (the UI binds against the live <see cref="Vessel"/>'s
        /// vesselModules / crew portraits) we keep the destructive path so the
        /// player-visible state stays in lockstep with the wire.
        /// Ported from upstream Release/0_29_2 commit 346ef48a (Drew Banyai).
        /// </summary>
        private static bool IsProtoSwapEligibleScene(GameScenes scene)
            => scene == GameScenes.SPACECENTER || scene == GameScenes.EDITOR;

        /// <summary>
        /// Check vessels that must be loaded
        /// </summary>
        public void CheckVesselsToLoad()
        {
            if (HighLogic.LoadedScene < GameScenes.SPACECENTER) return;

            try
            {
                var protoSwapEligible = IsProtoSwapEligibleScene(HighLogic.LoadedScene);
                var expensiveReloadsRemaining = MaxExpensiveReloadsPerTick;

                foreach (var keyVal in VesselProtos)
                {
                    if (!keyVal.Value.TryPeek(out var vesselProto)) continue;
                    if (vesselProto.GameTime > TimeSyncSystem.UniversalTime) continue;

                    // Probe FlightGlobals BEFORE dequeuing so we can decide whether this
                    // tick will be cheap (proto-swap) or expensive (ProtoVessel.Load) and
                    // rate-limit only the latter. When we're over budget on the expensive
                    // path we leave the head peeked-but-not-dequeued so the very next
                    // CheckVesselsToLoad tick retries it in FIFO order — there is no risk
                    // of starvation because TryPeek is non-mutating.
                    var existingVessel = FlightGlobals.FindVessel(vesselProto.VesselId);
                    var willUseProtoSwap = protoSwapEligible && existingVessel != null && !vesselProto.ForceReload;

                    if (!willUseProtoSwap && expensiveReloadsRemaining <= 0)
                        continue;

                    keyVal.Value.TryDequeue(out _);

                    if (VesselRemoveSystem.VesselWillBeKilled(vesselProto.VesselId))
                    {
                        // Recycle on the kill-list path too, otherwise the proto buffer
                        // leaks back to the pool only on the success branches below.
                        keyVal.Value.Recycle(vesselProto);
                        continue;
                    }

                    var forceReload = vesselProto.ForceReload;
                    var protoVessel = vesselProto.CreateProtoVessel();
                    var vesselId = vesselProto.VesselId;
                    keyVal.Value.Recycle(vesselProto);

                    var verboseErrors = !VesselsUnableToLoad.Contains(vesselId);
                    if (protoVessel == null || !protoVessel.Validate(verboseErrors) || protoVessel.HasInvalidParts(verboseErrors))
                    {
                        VesselsUnableToLoad.Add(vesselId);
                        continue;
                    }

                    VesselsUnableToLoad.Remove(vesselId);

                    if (willUseProtoSwap)
                    {
                        // SPACECENTER / EDITOR fast path: pointer-swap protoVessel without
                        // destroying the live Vessel. No reload event fires here — the live
                        // Vessel was never touched, so any listener that did real work on
                        // reload would be running on a no-op. The flightState ProtoVessel
                        // list (source-of-truth for save / scene transition) is updated to
                        // the fresh proto. Fall through to the destructive path on failure
                        // so we don't silently drop a wire update.
                        if (VesselLoader.UpdateProtoInPlace(existingVessel, protoVessel))
                            continue;
                    }

                    // Expensive path — count against the per-tick budget pre-emptively.
                    // We refund the slot below if LoadVesselIntoGame's UnchangedEarlyOut
                    // fired, because that branch returns before ProtoVessel.Load is
                    // called and the cost was just a counts comparison + flightPlan
                    // copy. Failed and Reloaded both run the destructive Load and pay
                    // the full cost; FreshlyLoaded also pays full Load + part
                    // instantiation. Refunding the cheap early-out matters because the
                    // steady-state hot path is exactly drift-broadcast → already-matched
                    // → early-out; without the refund, two such broadcasts per tick
                    // would starve a third genuinely-expensive reload to the next tick.
                    expensiveReloadsRemaining--;

                    var outcome = VesselLoader.LoadVessel(protoVessel, forceReload);
                    switch (outcome)
                    {
                        case VesselLoadOutcome.FreshlyLoaded:
                            LunaLog.Log($"[LMP]: Vessel {protoVessel.vesselID} loaded");
                            //BUG-008 Phase A: defer the load event until PQS terrain has stabilised
                            //and the vessel has been snapped onto the high-LOD surface. AlignAndThen
                            //fires the callback synchronously when no realignment is required (most
                            //loads) — only the cold-PQS case takes the coroutine path. See
                            //docs/research/02-analysis/bug-008-pqs-spawn-altitude.md.
                            {
                                var loadedVessel = protoVessel.vesselRef;
                                PqsAlignmentRoutine.AlignAndThen(loadedVessel, () => VesselLoadEvent.onLmpVesselLoaded.Fire(loadedVessel));
                            }
                            break;
                        case VesselLoadOutcome.Reloaded:
                            LunaLog.Log($"[LMP]: Vessel {protoVessel.vesselID} reloaded");
                            VesselReloadEvent.onLmpVesselReloaded.Fire(protoVessel.vesselRef);
                            break;
                        case VesselLoadOutcome.UnchangedEarlyOut:
                            // Stock-matched early-out from LoadVesselIntoGame. Deliberately
                            // silent in KSP.log: no "reloaded" line and no VesselReloadEvent
                            // fire because nothing actually changed. Previously the bool
                            // collapse caused us to log + fire on every drift broadcast even
                            // when part/crew counts already matched, which polluted KSP.log
                            // and ran any future subscriber's reload work on a no-op.
                            // Refund the budget slot — see the pre-emptive-charge comment
                            // above for why this isn't symmetric with Failed.
                            expensiveReloadsRemaining++;
                            break;
                        case VesselLoadOutcome.Failed:
                            break;
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
        /// Call this method if you expect to do a lot of modifications to a vessel and you want to send it only once
        /// </summary>
        public void DelayedSendVesselMessage(Guid vesselId, float delayInSec, bool forceReload = false)
        {
            if (QueuedVesselsToSend.Contains(vesselId)) return;

            QueuedVesselsToSend.Add(vesselId);
            CoroutineUtil.StartDelayedRoutine("QueueVesselMessageAsPartsChanged", () =>
            {
                QueuedVesselsToSend.Remove(vesselId);

                LunaLog.Log($"[LMP]: Sending delayed proto vessel {vesselId}");
                MessageSender.SendVesselMessage(FlightGlobals.FindVessel(vesselId));
            }, delayInSec);
        }

        /// <summary>
        /// Removes a vessel from the system
        /// </summary>
        public void RemoveVessel(Guid vesselId)
        {
            VesselProtos.TryRemove(vesselId, out _);
        }

        #endregion
    }
}
