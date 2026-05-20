using KSP.UI.Screens.Flight;
using LmpClient.Extensions;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselPositionSys;
using System;
using Object = UnityEngine.Object;

namespace LmpClient.VesselUtilities
{
    /// <summary>
    /// What <see cref="VesselLoader.LoadVessel"/> actually did. Returned in place of a
    /// plain bool so callers (notably <c>VesselProtoSystem.CheckVesselsToLoad</c>) can
    /// distinguish "we destructively reloaded the live Vessel and any reload-event
    /// listeners should fire" from "the incoming proto matched the existing structure
    /// so we early-returned and nothing actually changed". The previous collapse to a
    /// single bool caused <c>[LMP]: Vessel ... reloaded</c> + the
    /// <c>VesselReloadEvent.onLmpVesselReloaded</c> fire to happen on every wire-side
    /// drift broadcast even when no destruction occurred, which (a) was misleading in
    /// KSP.log and (b) ran any future subscriber's reload work on a no-op.
    /// </summary>
    public enum VesselLoadOutcome
    {
        /// <summary>Validate() failed, Load() threw, or the proto produced a malformed orbit.</summary>
        Failed,
        /// <summary>No prior <see cref="Vessel"/> for this id; a brand-new one was created.</summary>
        FreshlyLoaded,
        /// <summary>An existing <see cref="Vessel"/> was destroyed and a replacement was created.</summary>
        Reloaded,
        /// <summary>An existing <see cref="Vessel"/> already matched the incoming structure — nothing was done.</summary>
        UnchangedEarlyOut,
    }

    public class VesselLoader
    {
        /// <summary>
        /// Loads/Reloads a vessel into game. See <see cref="VesselLoadOutcome"/> for the
        /// four possible outcomes; callers should only fire reload/load events on
        /// <see cref="VesselLoadOutcome.FreshlyLoaded"/> / <see cref="VesselLoadOutcome.Reloaded"/>.
        /// </summary>
        public static VesselLoadOutcome LoadVessel(ProtoVessel vesselProto, bool forceReload)
        {
            try
            {
                if (!vesselProto.Validate(true)) return VesselLoadOutcome.Failed;
                return LoadVesselIntoGame(vesselProto, forceReload);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error loading vessel: {e}");
                return VesselLoadOutcome.Failed;
            }
        }

        /// <summary>
        /// Cheap in-place proto swap for scenes where peer vessels are not physically
        /// visible (currently SPACECENTER and EDITOR). Updates the existing
        /// <see cref="Vessel"/>'s <c>protoVessel</c> backreference, replaces the entry
        /// in <c>flightState.protoVessels</c>, and points the new proto's
        /// <c>vesselRef</c> at the surviving <see cref="Vessel"/>. Does NOT destroy or
        /// re-instantiate any <see cref="UnityEngine.GameObject"/>, does NOT call
        /// <c>vesselProto.Load</c>, and does NOT trigger stock KSP's persistentId
        /// collision rewrite — which is what makes this a multi-frame-saving shortcut
        /// versus the full <see cref="LoadVessel"/> path. Safe only for unloaded /
        /// packed vessels, which is the steady state of every <see cref="Vessel"/> in
        /// FlightGlobals while the player is in SPACECENTER or EDITOR; in FLIGHT or
        /// TRACKSTATION the in-world vessel may be loaded and rendered, so callers
        /// MUST keep using the destructive <see cref="LoadVessel"/> there.
        /// Ported from upstream Release/0_29_2 commit 346ef48a (Drew Banyai).
        /// </summary>
        /// <returns>True when the swap was performed; false when the inputs were unusable.</returns>
        public static bool UpdateProtoInPlace(Vessel existingVessel, ProtoVessel newProto)
        {
            if (existingVessel == null || newProto == null) return false;
            if (HighLogic.CurrentGame?.flightState == null) return false;

            try
            {
                // Safety-net: verify newProto can serialise before we commit it to
                // flightState.protoVessels. Without this, a malformed wire-side proto
                // (e.g. a null resource definition left by a server mod, or a broken
                // DiscoveryInfo) would silently sit in flightState and then crash
                // GamePersistence.SaveGame() on the next autosave / scene transition
                // — freezing the UI on menu close. The destructive LoadVessel path
                // already runs this check; UpdateProtoInPlace must match that
                // discipline since it ALSO writes into flightState.protoVessels.
                try
                {
                    newProto.Save(new ConfigNode());
                }
                catch (Exception saveEx)
                {
                    LunaLog.LogWarning($"[LMP]: UpdateProtoInPlace refusing to swap unsaveable proto for " +
                                       $"{existingVessel.id}; falling back to destructive load. Error: {saveEx.Message}");
                    return false;
                }

                var protoVessels = HighLogic.CurrentGame.flightState.protoVessels;
                var vesselId = existingVessel.id;

                // Replace the old entry in flightState.protoVessels with newProto so any
                // subsequent save / scene-transition rebuild uses current wire data
                // instead of the now-stale proto we are about to detach.
                var replaced = false;
                if (protoVessels != null)
                {
                    for (var i = 0; i < protoVessels.Count; i++)
                    {
                        if (protoVessels[i] != null && protoVessels[i].vesselID == vesselId)
                        {
                            protoVessels[i] = newProto;
                            replaced = true;
                            break;
                        }
                    }
                    if (!replaced) protoVessels.Add(newProto);
                }

                existingVessel.protoVessel = newProto;
                newProto.vesselRef = existingVessel;
                return true;
            }
            catch (Exception e)
            {
                // Falls back to the caller's normal destructive path on next wire update.
                LunaLog.LogWarning($"[LMP]: UpdateProtoInPlace failed for {existingVessel.id}: {e.Message}");
                return false;
            }
        }

        #region Private methods

        /// <summary>
        /// Loads the vessel proto into the current game. Returns the outcome so the
        /// caller can distinguish a real destructive reload from an unchanged early-out
        /// (the latter previously masqueraded as a successful reload in logs / events).
        /// </summary>
        private static VesselLoadOutcome LoadVesselIntoGame(ProtoVessel vesselProto, bool forceReload)
        {
            if (HighLogic.CurrentGame?.flightState == null)
                return VesselLoadOutcome.Failed;

            var reloadingOwnVessel = FlightGlobals.ActiveVessel && vesselProto.vesselID == FlightGlobals.ActiveVessel.id;
            var hadExistingVessel = false;

            //In case the vessel exists, silently remove them from unity and recreate it again
            var existingVessel = FlightGlobals.FindVessel(vesselProto.vesselID);
            if (existingVessel != null)
            {
                hadExistingVessel = true;
                if (!forceReload && existingVessel.Parts.Count == vesselProto.protoPartSnapshots.Count &&
                    existingVessel.GetCrewCount() == vesselProto.GetVesselCrew().Count)
                {
                    // Always keep the stored flight plan current even when skipping a full reload.
                    // Without this, maneuver node changes are discarded and the vessel's
                    // PatchedConicSolver loads stale (empty) data on the next GoOffRails.
                    existingVessel.protoVessel.flightPlan = vesselProto.flightPlan;
                    return VesselLoadOutcome.UnchangedEarlyOut;
                }

                LunaLog.Log($"[LMP]: Reloading vessel {vesselProto.vesselID}");
                if (reloadingOwnVessel)
                    existingVessel.RemoveAllCrew();

                FlightGlobals.RemoveVessel(existingVessel);
                // Disable immediately so Unity stops calling FixedUpdate on this vessel before
                // Object.Destroy is processed — same deferred-destroy race that causes
                // Vessel.UpdateCaches() NullReferenceExceptions (see VesselRemoveSystem.KillVessel).
                existingVessel.gameObject.SetActive(false);
                foreach (var part in existingVessel.parts)
                {
                    Object.Destroy(part.gameObject);
                }
                Object.Destroy(existingVessel.gameObject);
            }
            else
            {
                LunaLog.Log($"[LMP]: Loading vessel {vesselProto.vesselID}");
            }

            SanitizePersistentIds(vesselProto);

            try
            {
                vesselProto.Load(HighLogic.CurrentGame.flightState);

                // [fix:BUG-023] Strip null entries from each ProtoPartSnapshot.protoModuleCrew
                // that stock KSP just appended when wire-side crew names failed to resolve
                // through CrewRoster. Has to run AFTER vesselProto.Load (which is what
                // populates protoModuleCrew from the wire ConfigNode) and BEFORE the
                // module-sanity walk + scene→FLIGHT transition. See ScrubInvalidProtoCrew
                // for the full root-cause rationale; ported from upstream Release/0_29_2
                // commit 138c2b3e (Drew Banyai).
                ScrubInvalidProtoCrew(vesselProto);
            }
            catch (Exception loadEx)
            {
                // KSP may have created the Vessel GameObject before the exception (e.g. OrbitSnapshot.Load
                // throws when the vessel's referenceBody index is out of range because the server has extra
                // celestial bodies from a mod the client doesn't have).  Without cleanup the zombie vessel
                // stays in FlightGlobals and causes NullReferenceExceptions in Vessel.UpdateCaches() on
                // every physics tick.
                LunaLog.LogError($"[LMP]: Vessel {vesselProto.vesselID} threw during ProtoVessel.Load — removing to prevent zombie vessel. Error: {loadEx.Message}");
                if (vesselProto.vesselRef != null)
                {
                    FlightGlobals.RemoveVessel(vesselProto.vesselRef);
                    foreach (var part in vesselProto.vesselRef.parts)
                        Object.Destroy(part.gameObject);
                    Object.Destroy(vesselProto.vesselRef.gameObject);
                }
                HighLogic.CurrentGame.flightState.protoVessels.Remove(vesselProto);
                return VesselLoadOutcome.Failed;
            }

            if (vesselProto.vesselRef == null)
            {
                LunaLog.Log($"[LMP]: Protovessel {vesselProto.vesselID} failed to create a vessel!");
                return VesselLoadOutcome.Failed;
            }

            // Verify that every part module loaded successfully.  When the server has a mod that the
            // client lacks, KSP may instantiate a part but leave null slots in Part.Modules — these
            // cause Vessel.UpdateCaches() to throw a NullReferenceException on every physics tick.
            if (vesselProto.vesselRef.parts != null)
            {
                string badDetail = null;
                for (var pi = 0; pi < vesselProto.vesselRef.parts.Count && badDetail == null; pi++)
                {
                    var p = vesselProto.vesselRef.parts[pi];
                    if (p == null) { badDetail = $"null part at index {pi}"; break; }
                    if (p.Modules == null) continue;
                    for (var mi = 0; mi < p.Modules.Count; mi++)
                    {
                        if (p.Modules[mi] == null)
                        {
                            badDetail = $"null module at index {mi} on part '{p.partName}'";
                            break;
                        }
                    }
                }

                if (badDetail != null)
                {
                    LunaLog.LogError($"[LMP]: Vessel {vesselProto.vesselID} ({vesselProto.vesselName}) loaded with {badDetail} — removing to prevent Vessel.UpdateCaches NullReferenceException spam.");
                    FlightGlobals.RemoveVessel(vesselProto.vesselRef);
                    vesselProto.vesselRef.gameObject.SetActive(false);
                    foreach (var p in vesselProto.vesselRef.parts)
                        if (p?.gameObject != null) Object.Destroy(p.gameObject);
                    Object.Destroy(vesselProto.vesselRef.gameObject);
                    HighLogic.CurrentGame.flightState.protoVessels.Remove(vesselProto);
                    return VesselLoadOutcome.Failed;
                }
            }

            // Safety-net: verify the ProtoVessel can be saved before keeping it in the flight state.
            // If ProtoVessel.Save() throws (e.g. from a null resource definition left by a server mod),
            // GamePersistence.SaveGame() would also throw, causing the UI to freeze on any menu close.
            try
            {
                vesselProto.Save(new ConfigNode());
            }
            catch (Exception saveEx)
            {
                LunaLog.LogError($"[LMP]: Vessel {vesselProto.vesselID} ({vesselProto.vesselName}) cannot be saved — removing to prevent UI freezes. Error: {saveEx.Message}");
                FlightGlobals.RemoveVessel(vesselProto.vesselRef);
                foreach (var part in vesselProto.vesselRef.parts)
                    Object.Destroy(part.gameObject);
                Object.Destroy(vesselProto.vesselRef.gameObject);
                HighLogic.CurrentGame.flightState.protoVessels.Remove(vesselProto);
                return VesselLoadOutcome.Failed;
            }

            VesselPositionSystem.Singleton.ForceUpdateVesselPosition(vesselProto.vesselRef.id);

            vesselProto.vesselRef.protoVessel = vesselProto;
            if (vesselProto.vesselRef.isEVA)
            {
                var evaModule = vesselProto.vesselRef.FindPartModuleImplementing<KerbalEVA>();
                if (evaModule != null && evaModule.fsm != null && !evaModule.fsm.Started)
                {
                    evaModule.fsm?.StartFSM("Idle (Grounded)");
                }
                vesselProto.vesselRef.GoOnRails();
            }

            if (vesselProto.vesselRef.situation > Vessel.Situations.PRELAUNCH)
            {
                vesselProto.vesselRef.orbitDriver.updateFromParameters();
            }

            if (double.IsNaN(vesselProto.vesselRef.orbitDriver.pos.x))
            {
                LunaLog.Log($"[LMP]: Protovessel {vesselProto.vesselID} has an invalid orbit");
                return VesselLoadOutcome.Failed;
            }

            if (reloadingOwnVessel)
            {
                vesselProto.vesselRef.Load();
                vesselProto.vesselRef.RebuildCrewList();

                //Do not do the setting of the active vessel manually, too many systems are dependant of the events triggered by KSP
                FlightGlobals.ForceSetActiveVessel(vesselProto.vesselRef);

                vesselProto.vesselRef.SpawnCrew();
                foreach (var crew in vesselProto.vesselRef.GetVesselCrew())
                {
                    ProtoCrewMember._Spawn(crew);
                    if (crew.KerbalRef)
                        crew.KerbalRef.state = Kerbal.States.ALIVE;
                }

                if (KerbalPortraitGallery.Instance.ActiveCrewItems.Count != vesselProto.vesselRef.GetCrewCount())
                {
                    KerbalPortraitGallery.Instance.StartReset(FlightGlobals.ActiveVessel);
                }
            }

            // Only the destructive-reload branch and the brand-new-vessel branch reach
            // here; the structure-matches early-out returned UnchangedEarlyOut above.
            return hadExistingVessel ? VesselLoadOutcome.Reloaded : VesselLoadOutcome.FreshlyLoaded;
        }

        #endregion

        #region ID sanitization

        /// <summary>
        /// Proactively remaps any persistentId values in vesselProto that already exist in the
        /// running FlightGlobals registries (PersistentVesselIds, PersistentLoadedPartIds,
        /// PersistentUnloadedPartIds) before the vessel is loaded into the game.
        ///
        /// Without this, KSP's HandlePartPersistentIdCollision fires O(n) times per conflicting
        /// part on the main thread, which under concurrent LMP vessel loads can cascade into a
        /// freeze when many parts collide simultaneously.  By remapping upfront using
        /// FlightGlobals.GetUniquepersistentId() we hand KSP clean IDs and the collision handler
        /// never fires.
        ///
        /// The incoming proto IDs are transient transport values — they only need to be unique on
        /// this client.  The authoritative state is the server's save, so remapping here is safe.
        /// </summary>
        private static void SanitizePersistentIds(ProtoVessel vesselProto)
        {
            // Strip null crew slots before load — Vessel.Start() calls RebuildCrewList() which
            // iterates protoModuleCrew on every ProtoPartSnapshot; a null slot causes a
            // NullReferenceException that Unity catches internally (never reaches our catch block).
            foreach (var snapshot in vesselProto.protoPartSnapshots)
                snapshot.protoModuleCrew?.RemoveAll(c => c == null);

            // Vessel-level persistentId
            if (FlightGlobals.PersistentVesselIds.ContainsKey(vesselProto.persistentId))
            {
                var newId = FlightGlobals.GetUniquepersistentId();
                LunaLog.Log($"[LMP]: PersistentId collision — remapping vessel {vesselProto.vesselID} " +
                            $"vessel persistentId {vesselProto.persistentId} → {newId}");
                vesselProto.persistentId = newId;
            }

            // Per-part persistentId (ProtoPartSnapshot)
            foreach (var part in vesselProto.protoPartSnapshots)
            {
                if (FlightGlobals.PersistentLoadedPartIds.ContainsKey(part.persistentId) ||
                    FlightGlobals.PersistentUnloadedPartIds.ContainsKey(part.persistentId))
                {
                    var newId = FlightGlobals.GetUniquepersistentId();
                    LunaLog.Log($"[LMP]: PersistentId collision — remapping vessel {vesselProto.vesselID} " +
                                $"part {part.partName} persistentId {part.persistentId} → {newId}");
                    part.persistentId = newId;
                }
            }
        }

        /// <summary>
        /// [fix:BUG-023] Removes null entries from every <see cref="ProtoPartSnapshot.protoModuleCrew"/>
        /// list on the freshly-loaded protovessel.
        ///
        /// Stock KSP's <c>ProtoPartSnapshot.LoadCrew</c> appends a <c>null</c> placeholder to
        /// <c>protoModuleCrew</c> whenever a wire-side <c>crew = NAME</c> value cannot be resolved
        /// through <c>HighLogic.CurrentGame.CrewRoster</c> — typically because the originating client
        /// serialised an empty name, or because a kerbal that exists on their roster hasn't yet been
        /// replicated to ours (KerbalProto messages and VesselProto messages race; if the vessel
        /// arrives first, the proto's crew names resolve against an incomplete local roster).
        ///
        /// Those null placeholders sit dormant on the proto until something walks them, at which
        /// point three different stock KSP code paths NRE in distinct ways — all three observed
        /// in BUG-023 reports:
        ///
        ///   1. <c>KSP.UI.Screens.KbApp_VesselCrew.CreateVesselCrewList</c> in the Tracking Station
        ///      pulls crew via <c>vessel.protoVessel.protoPartSnapshots[*].protoModuleCrew</c> and
        ///      <see cref="System.Collections.Generic.List{T}.Sort(System.Comparison{T})"/>s the
        ///      result by <c>KbApp_VesselCrew.CompareSeatIdx</c>, which dereferences <c>r1.seatIdx</c>
        ///      / <c>r2.seatIdx</c>. Sort rethrows the NRE as
        ///      <c>InvalidOperationException: Failed to compare two elements in the array</c>
        ///      when the user clicks the vessel — freezing the Tracking-Station info pane. This is
        ///      the "kerbal does not exist in astronaut complex" symptom reported in issues #576 and
        ///      #603 (the AC is the same info-pane surface as the Tracking Station crew listing).
        ///   2. On scene transition to FLIGHT, <c>ProtoVessel.LoadObjects</c> →
        ///      <c>ProtoPartSnapshot.ConfigurePart</c> → <c>Part.RegisterCrew</c> dereferences each
        ///      <c>protoModuleCrew</c> entry to register seat assignments and NREs out of
        ///      <c>FlightDriver.Start</c>, leaving the player on a black flight scene.
        ///   3. The half-instantiated vessel's <c>ModuleCommand.UpdateControlSourceState</c> then
        ///      NREs every FixedUpdate forever once <c>FixedUpdate</c> resumes.
        ///
        /// All three preconditions are null entries inside <c>protoModuleCrew</c>. Removing them
        /// here defangs all three at once. The local game state after the scrub is identical to what
        /// stock KSP would have produced if the wire data simply omitted those crew entries to
        /// begin with: the seat is empty, the part is crewless from this client's perspective, and
        /// the originating peer's vessel-update stream remains the source of truth for who is
        /// actually aboard. We deliberately do not invent placeholder kerbals because that would
        /// (a) leak fake names into the local roster when stock KSP next saved, and (b) re-collide
        /// with the real kerbals when the wire data finally caught up.
        ///
        /// IMPORTANT — parallel-list invariant: <see cref="ProtoPartSnapshot"/> carries
        /// <c>protoModuleCrew</c> (<c>List&lt;ProtoCrewMember&gt;</c>) and <c>protoCrewNames</c>
        /// (<c>List&lt;string&gt;</c>) as parallel arrays indexed in lockstep. Stock
        /// <c>KerbalRoster.ValidateAssignments(Game)</c> walks both by the same index <c>i</c>
        /// (loop bound is <c>protoModuleCrew.Count</c>, lookup is <c>protoCrewNames[i]</c>) and on
        /// the missing-from-roster branch calls <c>SystemUtilities.ExpungeKerbal(protoModuleCrew[i])</c>.
        /// If we strip nulls from <c>protoModuleCrew</c> alone the indices shift and a subsequent
        /// validation pass — which fires on every game / scene load triggered by the post-Game.Save
        /// autosave round-trip — would call <c>ExpungeKerbal</c> on an unrelated, real kerbal that
        /// now lives at the same index as a still-unresolved name. That would silently delete real
        /// crew. We therefore remove matched (crew slot, name slot) pairs in lockstep, keeping the
        /// two-list invariant intact for every downstream stock consumer.
        ///
        /// Ported from upstream Release/0_29_2 commit 138c2b3e (Drew Banyai). Defense-in-depth: this
        /// strip-at-load is paired with the <c>Part_RegisterCrew</c> + <c>KnowledgeBase_GetVesselCrewByAvailablePart</c>
        /// Harmony patches that catch the same shape if the autosave round-trip re-introduces nulls
        /// later (the patches reference this method by name in their XML-doc).
        /// </summary>
        private static void ScrubInvalidProtoCrew(ProtoVessel vesselProto)
        {
            if (vesselProto?.protoPartSnapshots == null) return;
            try
            {
                var totalRemoved = 0;
                var partsAffected = 0;
                var nameDesyncs = 0;
                for (var p = 0; p < vesselProto.protoPartSnapshots.Count; p++)
                {
                    var snapshot = vesselProto.protoPartSnapshots[p];
                    var crew = snapshot?.protoModuleCrew;
                    if (crew == null || crew.Count == 0) continue;

                    var names = snapshot.protoCrewNames;
                    var removedHere = 0;

                    // Walk back-to-front so RemoveAt does not invalidate indices for entries
                    // we haven't visited yet.
                    for (var i = crew.Count - 1; i >= 0; i--)
                    {
                        if (crew[i] != null) continue;

                        crew.RemoveAt(i);
                        if (names != null)
                        {
                            if (i < names.Count) names.RemoveAt(i);
                            else nameDesyncs++;
                        }
                        removedHere++;
                    }

                    if (removedHere > 0)
                    {
                        totalRemoved += removedHere;
                        partsAffected++;
                    }
                }

                if (totalRemoved > 0)
                {
                    LunaLog.LogWarning(
                        $"[LMP][fix:BUG-023]: Scrubbed {totalRemoved} null protoModuleCrew entr{(totalRemoved == 1 ? "y" : "ies")} " +
                        $"(and parallel protoCrewNames slots) across {partsAffected} part(s) on vessel " +
                        $"{vesselProto.vesselID} ({vesselProto.vesselName ?? "<unknown>"}). " +
                        $"Caused by wire-side crew names that don't resolve in this client's CrewRoster (typical: empty " +
                        $"crew name or a kerbal proto that arrived after the vessel proto). Without this scrub stock KSP " +
                        $"NREs in KbApp_VesselCrew.CompareSeatIdx (Tracking Station focus / Astronaut Complex pane), " +
                        $"Part.RegisterCrew (scene→FLIGHT), and ModuleCommand.UpdateControlSourceState (every FixedUpdate); " +
                        $"without the lockstep removal, KerbalRoster.ValidateAssignments would later ExpungeKerbal on a " +
                        $"misindexed real kerbal.");

                    // [Stage 6 Phase 6.6] Snapshot the scrubbed count so the
                    // tracking-station / map / flight label surfaces can render
                    // "Crew: N (agency)" for foreign-agency vessels per spec §2
                    // Q-Render. The scrub removes exactly the kerbals whose names
                    // don't resolve in the local CrewRoster — under
                    // PerAgencyKerbalRosterEnabled (Phase 6.4 request filter), those
                    // are foreign-agency kerbals BY DEFINITION because the per-
                    // agency request filter prevents foreign kerbal files from
                    // ever reaching this client.
                    //
                    // Why the combined gate, not PerAgencyCareerEnabled alone: under
                    // the intermediate PerAgencyCareer=on / PerAgencyKerbalRoster=off
                    // configuration (typical Stage 5 → Stage 6 ramp), the roster is
                    // still shared, so foreign-vessel kerbal names ARE in the local
                    // roster eventually. BUG-023 KerbalProto-after-VesselProto race
                    // windows still produce transient scrubs of those local-but-not-
                    // yet-arrived names. Populating the registry on those transients
                    // would seed misleading "Crew: N (Acme Astronautics)" labels
                    // implying Acme owns kerbals that are actually shared — and the
                    // eviction-on-clean-scrub branch below wouldn't fire if no
                    // subsequent VesselProto reload arrives (UnchangedEarlyOut path).
                    // Gating on the combined Phase 6.6 flag means the registry only
                    // populates when the scrub is genuinely the per-agency partition
                    // doing its job.
                    if (SettingsSystem.ServerSettings.PerAgencyKerbalRosterEnabled)
                    {
                        AgencySystem.Singleton.ForeignCrewCount[vesselProto.vesselID] = totalRemoved;
                    }
                }
                else
                {
                    // [Stage 6 Phase 6.6] Symmetric eviction: a vessel that
                    // previously had foreign crew recorded above but now scrubs
                    // cleanly has either lost all foreign crew (EVA / death /
                    // disembark) or — under the eventual transferagency push —
                    // had its ownership flip to the local agency in a future
                    // re-load. The eviction is a no-op on vessels that never
                    // had a registry entry (TryRemove on missing key is safe);
                    // mentioned in [[feedback-cascade-race-same-lock-recheck]]
                    // discipline that "symmetric write/evict at the snapshot
                    // site beats trying to invalidate from N consumers."
                    //
                    // Same combined-gate guard as the population branch — under
                    // gate=off the registry is never written and the eviction
                    // is unreachable. The render-time IsForeignVessel check in
                    // LabelEvents is the SECOND line of defense against staleness
                    // (covers the case where the vessel was scrubbed-clean once
                    // under gate=on but the gate later flipped off, leaving the
                    // entry behind — eviction here doesn't fire because the
                    // gate flipped before the next scrub).
                    if (SettingsSystem.ServerSettings.PerAgencyKerbalRosterEnabled)
                    {
                        AgencySystem.Singleton.ForeignCrewCount.TryRemove(vesselProto.vesselID, out _);
                    }
                }

                if (nameDesyncs > 0)
                {
                    LunaLog.LogWarning(
                        $"[LMP][fix:BUG-023]: ScrubInvalidProtoCrew encountered {nameDesyncs} crew slot(s) on vessel " +
                        $"{vesselProto.vesselID} where protoModuleCrew was longer than protoCrewNames before the scrub. " +
                        $"This means the proto already arrived with a broken parallel-list invariant; the live " +
                        $"protoModuleCrew has been cleaned but stock KerbalRoster.ValidateAssignments will silently " +
                        $"skip the surplus indices since its loop bound is now the (shorter) crew list.");
                }
            }
            catch (Exception e)
            {
                //Diagnostic / defensive cleanup must never break the load path.
                LunaLog.LogWarning($"[LMP][fix:BUG-023]: ScrubInvalidProtoCrew failed for {vesselProto.vesselID}: {e.Message}");
            }
        }

        #endregion
    }
}
