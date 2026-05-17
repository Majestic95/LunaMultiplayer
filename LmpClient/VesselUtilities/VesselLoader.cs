using KSP.UI.Screens.Flight;
using LmpClient.Extensions;
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

        #endregion
    }
}
