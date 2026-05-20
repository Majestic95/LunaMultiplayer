using LunaConfigNode.CfgNode;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace Server.System.Vessel
{
    /// <summary>
    /// We try to avoid working with protovessels as much as possible as they can be huge files.
    /// This class patches the vessel file with the information messages we receive about a position and other vessel properties.
    /// This way we send the whole vessel definition only when there are parts that have changed 
    /// </summary>
    public partial class VesselDataUpdater
    {
        #region Semaphore

        /// <summary>
        /// To not overwrite our own data we use a lock
        /// </summary>
        private static readonly ConcurrentDictionary<Guid, object> Semaphore = new ConcurrentDictionary<Guid, object>();

        /// <summary>
        /// Returns the canonical per-vessel writer lock object used by
        /// <see cref="RawConfigNodeInsertOrUpdate"/> to gate vessel field
        /// writes (S4 retro-review precedent, see line ~88). Exposed for
        /// admin-command authors who mutate <c>Vessel.OwningAgencyId</c>
        /// outside the proto-ingest path (Phase 3 Slice E-2
        /// <c>SetVesselAgencyCommand</c>) — they MUST hold this lock around
        /// the field write or risk a torn-write race against the proto
        /// ingest. Mirrors the
        /// <see cref="Scenario.ScenarioDataUpdater.GetSemaphore"/> BUG-033
        /// design template. Idempotent on the <see cref="ConcurrentDictionary"/>
        /// key so concurrent callers receive the same object.
        /// </summary>
        public static object GetVesselLock(Guid vesselId) =>
            Semaphore.GetOrAdd(vesselId, _ => new object());

        #endregion

        /// <summary>
        /// Sets ORBIT IDENT from the reference body name when provided (e.g. from position or update messages).
        /// </summary>
        internal static void ApplyOrbitIdent(Classes.Vessel vessel, string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return;

            if (vessel.Orbit.Exists("IDENT"))
                vessel.Orbit.Update("IDENT", bodyName);
            else
                vessel.Orbit.Add(new CfgNodeValue<string, string>("IDENT", bodyName));
        }

        /// <summary>
        /// Raw updates a vessel in the dictionary and takes care of the locking in case we received another vessel message type.
        /// <paramref name="clientSubspaceId"/> is stamped onto the vessel as its
        /// <see cref="Classes.Vessel.AuthoritativeSubspaceId"/> (BUG-005/006). The cross-subspace
        /// rejection itself is performed synchronously by <see cref="Server.Message.VesselMsgReader.HandleVesselProto"/>
        /// before this call so that the relay is suppressed on rejection; here we only stamp.
        ///
        /// <paramref name="senderOwningAgencyId"/> is the agency the proto sender belongs to under
        /// per-agency career mode (Stage 5.16b). <see cref="Guid.Empty"/> when the gate is off or
        /// the sender has no agency — no stamp in that case. When non-empty, the stamp follows
        /// "first owner wins": if an existing stored vessel already carries a non-empty
        /// <see cref="Classes.Vessel.OwningAgencyId"/>, the existing value is preserved and the
        /// sender's id is ignored. This implements the spec §10 Q3 rule that ownership transfer
        /// is admin-only (Stage 5.18d <c>transferagency</c>) and not implicit on re-proto. The
        /// incoming proto's own <see cref="Classes.Vessel.OwningAgencyId"/> (if any) is also
        /// ignored — server-side knowledge of (existing or sender's) agency is authoritative.
        /// </summary>
        public static void RawConfigNodeInsertOrUpdate(Guid vesselId, string vesselDataInConfigNodeFormat, int clientSubspaceId, Guid senderOwningAgencyId)
        {
            _ = Task.Run(() =>
            {
                // Retro-review S6: the fire-and-forget Task.Run body had no top-level try/catch,
                // so a parse failure or sanitiser fault would surface only via
                // TaskScheduler.UnobservedTaskException at GC. Wrap so failures show up in the log.
                try
                {
                    var vessel = new Classes.Vessel(vesselDataInConfigNodeFormat);
                    // Server-side parts-allowlist gate. Mirrors the client-side IsPartAllowed
                    // wildcard semantics (LmpClient/Systems/Mod/ModSystem.cs):
                    //   - Skip entirely if ModControl=false (the new default — drift-class fix
                    //     described in GeneralSettingsDefinition.ModControl XmlComment).
                    //   - Skip when ModFileSystem.ModControl is null (operator flipped ModControl=true
                    //     at runtime via /changesettings without restart → LoadModFile never ran).
                    //   - Skip when AllowedParts is null/empty (operator cleared <AllowedParts/> in
                    //     LMPModControl.xml for wildcard-via-mod-control semantics). The prior
                    //     .Except(emptyList) call returned ALL inputs → rejected every vessel-proto
                    //     under that recovery path. Caught by upgrade-lens review on 2026-05-20.
                    var allowedParts = ModFileSystem.ModControl?.AllowedParts;
                    if (GeneralSettings.SettingsStore.ModControl && allowedParts != null && allowedParts.Count > 0)
                    {
                        var vesselParts = vessel.Parts.GetAllValues().Select(p => p.Fields.GetSingle("name").Value);
                        var bannedParts = vesselParts.Except(allowedParts);
                        if (bannedParts.Any())
                        {
                            LunaLog.Warning($"Received a vessel with BANNED parts! {vesselId}");
                            return;
                        }
                    }
                    //BUG-013: rewrite localised stateString fields back to canonical English BEFORE the
                    //vessel lands in CurrentVessels, so neither the universe-on-disk copy nor any
                    //downstream relay carries the bad payload.
                    VesselSanitizer.Sanitize(vessel, vesselId.ToString());
                    //BUG-005/006: stamp the contributing client's subspace as the new authority.
                    //Sentinels (subspaceId <= 0) are not stamped — they leave existing authority in place
                    //so a warping or unidentified client cannot blank a vessel's authority.
                    if (clientSubspaceId > 0)
                    {
                        vessel.AuthoritativeSubspaceId = clientSubspaceId;
                    }
                    lock (Semaphore.GetOrAdd(vesselId, new object()))
                    {
                        // Retro-review S4: auth-preserve must happen INSIDE the per-vessel
                        // semaphore — the previous version read CurrentVessels outside the
                        // lock, so a racing update could change the existing entry's auth
                        // between our TryGetValue and the AddOrUpdate write.
                        VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var existingStored);
                        if (clientSubspaceId <= 0
                            && existingStored != null
                            && existingStored.AuthoritativeSubspaceId > 0)
                        {
                            vessel.AuthoritativeSubspaceId = existingStored.AuthoritativeSubspaceId;
                        }
                        //[Stage 5.16b] OwningAgency stamp under per-agency career mode.
                        //
                        //Existing vessels: ownership is sticky. Whatever OwningAgencyId is stored
                        //wins, INCLUDING Guid.Empty (which is the spec §10 Q3 "Unassigned sentinel"
                        //for pre-0.31 vessels imported into a per-agency universe). The previous
                        //version of this branch only preserved NON-Empty existing values and fell
                        //through to "stamp the sender" when existing==Empty — which silently mass-
                        //assigned every pre-existing vessel to whoever happened to send the first
                        //proto, contradicting Q3's "operator transfers via admin command" rule.
                        //Round-5 upgrade-lens review caught the bug; see the Vessel.cs OwningAgencyId
                        //XML for the matching doc-side commitment.
                        //
                        //New vessels: first proto under gate-on stamps the sender's agency. New
                        //vessels with a sender that has no agency (theoretical — auth path always
                        //registers) fall through to Empty.
                        //
                        //Dual-mode silence (spec §11): the entire stamp block is gated on
                        //AgencySystem.PerAgencyEnabled — the combined check for PerAgencyCareer=true
                        //AND GameMode=Career (Stage 5.17e-1, spec §10 Q-Mode Career-only sign-off).
                        //With the gate off (either condition), the field is left untouched — not
                        //written, not scrubbed. Pre-0.31 vessels stay clean; new vessels get no
                        //lmpOwningAgency field on disk.
                        //
                        //Wire-payload authority: under gate=on the wire-supplied OwningAgencyId is
                        //fully replaced with the server's authoritative computation — a client
                        //cannot spoof ownership at ingest time. The relayed bytes shipped to other
                        //clients (one statement below in VesselMsgReader) still carry the wire-
                        //supplied value; see the relay-bytes contract note there for the Stage
                        //5.18a client-mirror obligations.
                        if (Agency.AgencySystem.PerAgencyEnabled)
                        {
                            if (existingStored != null)
                            {
                                vessel.OwningAgencyId = existingStored.OwningAgencyId;
                            }
                            else if (senderOwningAgencyId != Guid.Empty)
                            {
                                vessel.OwningAgencyId = senderOwningAgencyId;
                            }
                            else
                            {
                                vessel.OwningAgencyId = Guid.Empty;
                            }
                        }
                        VesselStoreSystem.CurrentVessels.AddOrUpdate(vesselId, vessel, (key, existingVal) => vessel);
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[vessel] proto ingest failed for {vesselId}: {e}");
                }
            });
        }

        /// <summary>
        /// Drop the per-vessel lock object so the <see cref="Semaphore"/> dictionary doesn't
        /// grow without bound across the lifetime of a long-running server. Called by
        /// <see cref="VesselStoreSystem.RemoveVessel"/> after the vessel is removed from
        /// <see cref="VesselStoreSystem.CurrentVessels"/>. Retro-review S5.
        /// </summary>
        public static void ForgetVessel(Guid vesselId)
        {
            Semaphore.TryRemove(vesselId, out _);
        }
    }
}
