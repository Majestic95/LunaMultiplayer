using KSP.UI.Screens.Flight;
using LmpClient.Extensions;
using LmpClient.Systems.VesselPositionSys;
using System;
using System.Linq;
using Object = UnityEngine.Object;

namespace LmpClient.VesselUtilities
{
    public class VesselLoader
    {
        /// <summary>
        /// Loads/Reloads a vessel into game
        /// </summary>
        public static bool LoadVessel(ProtoVessel vesselProto, bool forceReload)
        {
            try
            {
                LogProtoVesselSummary(vesselProto, forceReload);
                return vesselProto.Validate(true) && LoadVesselIntoGame(vesselProto, forceReload);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error loading vessel: {e}");
                return false;
            }
        }

        /// <summary>
        /// Logs a single-line summary of an incoming protovessel before <see cref="ProtoVessel.Load"/>
        /// runs. Provides a baseline to correlate against KSP-side <c>Vessel.UpdateCaches()</c> /
        /// <c>CommNetVessel.UpdateComm()</c> NullReferenceExceptions: the offending vessel can be
        /// matched back to the most recent load by id, name, type, and the distinct part-name set
        /// (which fingerprints which mod set the originating client expected).
        /// </summary>
        private static void LogProtoVesselSummary(ProtoVessel vesselProto, bool forceReload)
        {
            if (vesselProto == null) return;
            try
            {
                var partCount = vesselProto.protoPartSnapshots?.Count ?? 0;
                var distinctParts = vesselProto.protoPartSnapshots == null
                    ? string.Empty
                    : string.Join(",", vesselProto.protoPartSnapshots.Select(p => p.partName).Distinct());
                LunaLog.Log($"[LMP]: Loading proto vessel {vesselProto.vesselID} ({vesselProto.vesselName}) " +
                            $"type={vesselProto.vesselType} situation={vesselProto.situation} " +
                            $"forceReload={forceReload} parts={partCount} distinctParts=[{distinctParts}]");
            }
            catch (Exception e)
            {
                //Diagnostic logging must never break the load path.
                LunaLog.LogWarning($"[LMP]: LogProtoVesselSummary failed for {vesselProto.vesselID}: {e.Message}");
            }
        }

        /// <summary>
        /// One-shot sanity walk of the freshly-loaded vessel that <see cref="ProtoVessel.Load"/> just
        /// produced. Detects the exact corruption shapes that cause stock KSP to NRE on every
        /// FixedUpdate inside <c>Vessel.UpdateCaches()</c> and inside <c>CommNetVessel.UpdateComm()</c>:
        /// null entries in <c>vessel.parts</c>, parts with null <c>partInfo</c>, parts with a missing
        /// <c>vessel</c> back-reference, parts with a null <c>Modules</c> collection, modules whose
        /// runtime count diverges from the proto's module count, and null entries in
        /// <c>protoModuleCrew</c>. Pure logging — does not change which vessels survive load.
        /// </summary>
        private static void LogPostLoadVesselSanity(ProtoVessel vesselProto)
        {
            var v = vesselProto?.vesselRef;
            if (v == null) return;
            try
            {
                if (v.parts == null)
                {
                    LunaLog.LogError($"[LMP]: Post-load sanity: vessel {v.id} ({v.vesselName}) has a NULL parts list.");
                    return;
                }

                int nullParts = 0, nullPartInfo = 0, nullVesselRef = 0, nullModules = 0, moduleCountMismatch = 0, nullCrewSlot = 0;
                for (var i = 0; i < v.parts.Count; i++)
                {
                    var part = v.parts[i];
                    if (part == null)
                    {
                        nullParts++;
                        continue;
                    }

                    if (part.partInfo == null) nullPartInfo++;
                    if (!part.vessel) nullVesselRef++;
                    if (part.Modules == null)
                    {
                        nullModules++;
                    }
                    else
                    {
                        var protoPart = i < vesselProto.protoPartSnapshots.Count ? vesselProto.protoPartSnapshots[i] : null;
                        if (protoPart?.modules != null && protoPart.modules.Count != part.Modules.Count)
                            moduleCountMismatch++;
                    }
                    if (part.protoModuleCrew != null && part.protoModuleCrew.Any(c => c == null))
                        nullCrewSlot++;
                }

                if (nullParts + nullPartInfo + nullVesselRef + nullModules + moduleCountMismatch + nullCrewSlot > 0)
                {
                    LunaLog.LogError($"[LMP]: Post-load sanity: vessel {v.id} ({v.vesselName}) is CORRUPT - " +
                                     $"nullParts={nullParts} nullPartInfo={nullPartInfo} nullVesselRef={nullVesselRef} " +
                                     $"nullModules={nullModules} moduleCountMismatch={moduleCountMismatch} nullCrewSlot={nullCrewSlot}. " +
                                     $"This vessel will likely NRE in Vessel.UpdateCaches()/CommNetVessel.UpdateComm() every FixedUpdate.");
                }
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: LogPostLoadVesselSanity failed for {v.id}: {e.Message}");
            }
        }

        #region Private methods

        /// <summary>
        /// Loads the vessel proto into the current game
        /// </summary>
        private static bool LoadVesselIntoGame(ProtoVessel vesselProto, bool forceReload)
        {
            if (HighLogic.CurrentGame?.flightState == null)
                return false;

            var reloadingOwnVessel = FlightGlobals.ActiveVessel && vesselProto.vesselID == FlightGlobals.ActiveVessel.id;

            //In case the vessel exists, silently remove them from unity and recreate it again
            var existingVessel = FlightGlobals.FindVessel(vesselProto.vesselID);
            if (existingVessel != null)
            {
                if (!forceReload && existingVessel.Parts.Count == vesselProto.protoPartSnapshots.Count &&
                    existingVessel.GetCrewCount() == vesselProto.GetVesselCrew().Count)
                {
                    return true;
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

            vesselProto.Load(HighLogic.CurrentGame.flightState);
            if (vesselProto.vesselRef == null)
            {
                LunaLog.Log($"[LMP]: Protovessel {vesselProto.vesselID} failed to create a vessel!");
                return false;
            }

            LogPostLoadVesselSanity(vesselProto);

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
                return false;
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

            return true;
        }

        #endregion
    }
}
