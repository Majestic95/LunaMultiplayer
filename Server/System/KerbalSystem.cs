using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Properties;
using Server.Server;
using Server.System.Agency;
using System;
using System.IO;
using System.Linq;

namespace Server.System
{
    public class KerbalSystem
    {
        // Expression-bodied property (not a `static readonly` field) so the path
        // re-resolves whenever `ServerContext.UniverseDirectory` is mutated —
        // required for ServerTest's per-test temp UniverseDirectory pattern
        // (`Path.GetTempPath() + "/lmp-<guid>"`). A `static readonly` field
        // would lock in the first value at type-init time, leaving tests
        // writing to a stale path AND breaking any future production code path
        // that reassigns UniverseDirectory at runtime. Mirrors the
        // <see cref="Server.System.Agency.AgencyState.AgenciesPath"/>
        // expression-bodied property pattern.
        public static string KerbalsPath => Path.Combine(ServerContext.UniverseDirectory, "Kerbals");

        public static void GenerateDefaultKerbals()
        {
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Jebediah Kerman.txt"), Resources.Jebediah_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Bill Kerman.txt"), Resources.Bill_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Bob Kerman.txt"), Resources.Bob_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Valentina Kerman.txt"), Resources.Valentina_Kerman);
        }

        public static void HandleKerbalProto(ClientStructure client, KerbalProtoMsgData data)
        {
            LunaLog.Debug($"Saving kerbal {data.Kerbal.KerbalName} from {client.PlayerName}");

            var path = Path.Combine(KerbalsPath, $"{data.Kerbal.KerbalName}.txt");
            FileHandler.WriteToFile(path, data.Kerbal.KerbalData, data.Kerbal.NumBytes);

            MessageQueuer.RelayMessage<KerbalSrvMsg>(client, data);
        }

        public static void HandleKerbalsRequest(ClientStructure client)
        {
            var sourcePath = ResolveKerbalsPathForRequester(client.PlayerName);

            // Materialise the file list AND read each file inside the same try
            // so a /deleteagency cascade interleaving anywhere in the pipeline
            // (between FolderExists, Directory.GetFiles, or per-file ReadFile)
            // is caught here rather than escaping to the receive thread.
            // Catching IOException (parent of DirectoryNotFoundException +
            // FileNotFoundException + UnauthorizedAccessException's IOException
            // base) covers every disk-vanish or permissions-flip the server
            // might hit during the read pipeline; the deferred enumeration
            // semantics of LINQ's Select would otherwise let a ReadFile throw
            // outside the catch.
            KerbalInfo[] kerbals;
            try
            {
                var kerbalFiles = FileHandler.GetFilesInPath(sourcePath);
                kerbals = kerbalFiles.Select(k =>
                {
                    var kerbalData = FileHandler.ReadFile(k);
                    return new KerbalInfo
                    {
                        KerbalData = kerbalData,
                        NumBytes = kerbalData.Length,
                        KerbalName = Path.GetFileNameWithoutExtension(k)
                    };
                }).ToArray();
            }
            catch (IOException ex)
            {
                LunaLog.Warning($"[fix:per-agency-kerbal-roster] Kerbal directory read failed for {client.PlayerName} at {sourcePath} (likely /deleteagency mid-handshake or operator file mutation): {ex.GetType().Name} {ex.Message}. Sending empty reply.");
                kerbals = Array.Empty<KerbalInfo>();
            }
            LunaLog.Debug($"Sending {client.PlayerName} {kerbals.Length} kerbals from {sourcePath}");

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<KerbalReplyMsgData>();
            msgData.Kerbals = kerbals;
            msgData.KerbalsCount = kerbals.Length;

            MessageQueuer.SendToClient<KerbalSrvMsg>(client, msgData);
        }

        /// <summary>
        /// [Stage 6 / Phase 6.4] Resolves the directory <see cref="HandleKerbalsRequest"/>
        /// should enumerate for the given requester. Branches:
        /// <list type="number">
        ///   <item><see cref="AgencySystem.PerAgencyKerbalRosterEnabled"/> is false →
        ///         <see cref="KerbalsPath"/> (legacy shared <c>Universe/Kerbals/</c>).
        ///         Dual-mode silence: identical to v7 behaviour.</item>
        ///   <item>Gate on AND <see cref="AgencySystem.AgencyByPlayerName"/> miss →
        ///         <see cref="KerbalsPath"/> + Warning. Defensive fallback per spec §3
        ///         ("Fall back to legacy path if AgencyByPlayerName lookup fails.").
        ///         Unreachable on a healthy server (RegisterAgency inserts the index
        ///         entry before HandshakeReply ships, and PerAgencyEnabled requires
        ///         Career mode where auto-register fires) but the fallback prevents
        ///         an empty roster on a torn registry state.</item>
        ///   <item>Gate on AND mapping resolves AND <see cref="FileHandler.FolderExists"/>
        ///         true → returns the agency's per-agency Kerbals subdir.</item>
        ///   <item>Gate on AND mapping resolves AND subdir absent →
        ///         <see cref="KerbalsPath"/> + Warning. Unreachable on a Phase-6.3-shipped
        ///         universe (mint creates it; load-backfill heals it), but keeps a
        ///         bricked install limping instead of throwing inside
        ///         <see cref="FileHandler.GetFilesInPath"/>.</item>
        /// </list>
        ///
        /// <para>Takes <c>string playerName</c> (not <see cref="ClientStructure"/>) so
        /// ServerTest can exercise every branch without constructing a
        /// <see cref="Lidgren.Network.NetConnection"/>. Same testability pattern as
        /// <see cref="Server.System.Agency.AgencySystem.GetKerbalsPathForAgency"/> and
        /// the Stage 5 / WOLF Slice F helper family.</para>
        /// </summary>
        internal static string ResolveKerbalsPathForRequester(string playerName)
        {
            if (!AgencySystem.PerAgencyKerbalRosterEnabled)
                return KerbalsPath;

            if (!AgencySystem.AgencyByPlayerName.TryGetValue(playerName, out var agencyId))
            {
                LunaLog.Warning($"[fix:per-agency-kerbal-roster] No AgencyByPlayerName mapping for '{playerName}' under PerAgencyKerbalRosterEnabled — falling back to legacy {KerbalsPath}. Either the registry is torn (handshake race) or the player has not yet auto-registered.");
                return KerbalsPath;
            }

            var agencyKerbals = AgencySystem.GetKerbalsPathForAgency(agencyId);
            if (!FileHandler.FolderExists(agencyKerbals))
            {
                LunaLog.Warning($"[fix:per-agency-kerbal-roster] Agency {agencyId:N} for '{playerName}' has no Kerbals subdir at {agencyKerbals} (Phase 6.3 lifecycle hook should have seeded it). Falling back to legacy {KerbalsPath}.");
                return KerbalsPath;
            }

            return agencyKerbals;
        }

        public static void HandleKerbalRemove(ClientStructure client, KerbalRemoveMsgData message)
        {
            var kerbalToRemove = message.KerbalName;

            // [Stage 5.17e-8] K1 kerbal-roster grief guard (spec §10 Q-Kerbal sign-off).
            // The v1 kerbal roster is shared across agencies (per-agency rosters are
            // Stage 6 work — wire-extension on every Kerbal*MsgData + per-agency
            // request filtering + original-Kerbal disambiguation). To prevent the
            // grief vector ("Bob's modded client despawns Alice's Jeb while Jeb is
            // mid-mission"), the server under per-agency mode refuses a remove of
            // a kerbal that's currently aboard a vessel NOT owned by the requester's
            // agency. Unassigned kerbals (KIA-cleared, never-launched, AC-pool) can
            // be removed by anyone — legitimate KSP-driven removes (KIA, EVA-rescue
            // completion) fire AFTER the kerbal is no longer crewing a vessel, so
            // the unassigned path covers them.
            if (AgencySystem.PerAgencyEnabled && !CanRemoveKerbalUnderK1(client, kerbalToRemove))
            {
                LunaLog.Warning($"[fix:per-agency-career] Refused KerbalRemove for {kerbalToRemove} from {client.PlayerName}: kerbal is currently aboard a vessel owned by a different agency. K1 v1 limitation — see spec §10 Q-Kerbal.");
                return;
            }

            LunaLog.Debug($"Removing kerbal {kerbalToRemove} from {client.PlayerName}");
            FileHandler.FileDelete(Path.Combine(KerbalsPath, $"{kerbalToRemove}.txt"));

            MessageQueuer.RelayMessage<KerbalSrvMsg>(client, message);
        }

        /// <summary>
        /// [Stage 5.17e-8] K1 grief-prevention scan. Returns false when the kerbal
        /// is currently aboard a vessel owned by an agency other than the requester's
        /// (and that vessel's agency is non-Empty — Unassigned-sentinel vessels per
        /// spec §10 Q3 allow any agency to interact). Returns true when the kerbal
        /// is unassigned (not aboard any vessel), when the requester is the vessel
        /// owner, when the requester has no agency mapping, or when the kerbal name
        /// is empty (defensive fall-through to legacy path).
        ///
        /// **Scan cost.** Iterates <see cref="VesselStoreSystem.CurrentVessels"/>
        /// and serializes each vessel's ConfigNode to text + scans for
        /// <c>"crew = {name}"</c>. O(N vessels * vessel-size) per remove. KerbalRemove
        /// is rare (a handful per session, mostly on KIA/EVA-rescue), so the cost
        /// is negligible at v1 cohort sizes. A future structured accessor on
        /// <see cref="Server.System.Vessel.Classes.Vessel"/> (Parts → crew[]) would
        /// be the natural optimization if the scan ever shows up in profiling.
        /// </summary>
        private static bool CanRemoveKerbalUnderK1(ClientStructure client, string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName))
                return true;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var requesterAgency))
                return true; // unmapped requester → fall through to legacy path

            var needle = "crew = " + kerbalName;
            foreach (var kvp in VesselStoreSystem.CurrentVessels)
            {
                var vessel = kvp.Value;
                string vesselText;
                try
                {
                    vesselText = VesselStoreSystem.GetVesselInConfigNodeFormat(kvp.Key);
                }
                catch (Exception)
                {
                    continue; // skip vessels that fail to serialize; don't deadlock the remove
                }
                if (string.IsNullOrEmpty(vesselText) || !vesselText.Contains(needle))
                    continue;

                // Kerbal is aboard this vessel.
                if (vessel.OwningAgencyId == Guid.Empty)
                    return true; // Unassigned-sentinel vessel — any agency may interact (spec §10 Q3)
                if (vessel.OwningAgencyId == requesterAgency)
                    return true; // Requester owns the vessel — allow.
                return false; // Different agency owns the vessel — refuse the remove.
            }

            // Kerbal not aboard any vessel — unassigned, allow remove.
            return true;
        }
    }
}
