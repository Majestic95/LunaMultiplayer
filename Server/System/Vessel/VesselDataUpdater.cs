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
                    if (GeneralSettings.SettingsStore.ModControl)
                    {
                        var vesselParts = vessel.Parts.GetAllValues().Select(p => p.Fields.GetSingle("name").Value);
                        var bannedParts = vesselParts.Except(ModFileSystem.ModControl.AllowedParts);
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
                        //[Stage 5.16b] OwningAgency: first-owner-wins. Existing value is preserved across
                        //subsequent protos (transfer is admin-only per spec §10 Q3); otherwise stamp the
                        //proto sender's agency when the gate is on. Same per-vessel semaphore as the
                        //AuthSubspace preserve branch — the existing-read must be inside the lock to
                        //avoid a TOCTOU between TryGetValue and AddOrUpdate.
                        //
                        //Dual-mode silence (spec §11): the entire stamp block is gated on PerAgencyCareer.
                        //With the gate off, the field is left untouched — i.e. NOT written, NOT scrubbed.
                        //Pre-0.31 vessels stay clean; new vessels get no lmpOwningAgency field on disk.
                        //Round-2 review caught the prior version's terminal scrub-to-Empty branch as a
                        //dual-mode-silence violation: it wrote the all-zero 32-char hex to every freshly
                        //ingested vessel under PerAgencyCareer=false, leaking the field onto the wire (via
                        //GetVesselInConfigNodeFormat sync replies) and onto disk (via BackupVessels).
                        //
                        //Wire-payload scrub (server-authoritative on incoming protos): when the gate IS
                        //on, the wire-supplied OwningAgencyId is replaced with the server's authoritative
                        //value (existing owner if any, else sender's agency, else Guid.Empty). A
                        //misbehaving client cannot spoof ownership — important because Stage 5.17a's
                        //LockSystem cross-agency rejection will gate on this field with real consequences,
                        //and there is no inbound equivalent of RejectIfPastSubspace to catch a bogus value
                        //at the protocol boundary. The gate-on scrub fall-through to Empty is for the
                        //case where the server cannot attribute the proto to any agency (sender just
                        //connected mid-flight and hasn't registered yet — should be impossible given the
                        //auth-then-OnPlayerAuthenticated ordering, but defensive).
                        if (GameplaySettings.SettingsStore.PerAgencyCareer)
                        {
                            if (existingStored != null && existingStored.OwningAgencyId != Guid.Empty)
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
