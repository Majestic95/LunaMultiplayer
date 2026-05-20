using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Interface;
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
            if (AgencySystem.PerAgencyKerbalRosterEnabled)
            {
                HandleKerbalProtoPerAgency(client, data);
                return;
            }

            LunaLog.Debug($"Saving kerbal {data.Kerbal.KerbalName} from {client.PlayerName}");

            var path = Path.Combine(KerbalsPath, $"{data.Kerbal.KerbalName}.txt");
            FileHandler.WriteToFile(path, data.Kerbal.KerbalData, data.Kerbal.NumBytes);

            MessageQueuer.RelayMessage<KerbalSrvMsg>(client, data);
        }

        /// <summary>
        /// [Stage 6 / Phase 6.5] Per-agency write-path branch of
        /// <see cref="HandleKerbalProto"/>. Delegates the disk-side work to
        /// the pure helper <see cref="TryWriteKerbalProtoPerAgency"/> (string +
        /// bytes inputs, directly unit-testable) and, on success, relays to
        /// same-agency clients via <see cref="RelayToSameAgencyClients{TSrvMsg}"/>.
        /// </summary>
        private static void HandleKerbalProtoPerAgency(ClientStructure client, KerbalProtoMsgData data)
        {
            if (TryWriteKerbalProtoPerAgency(
                    client.PlayerName,
                    data.Kerbal.KerbalName,
                    data.Kerbal.KerbalData,
                    data.Kerbal.NumBytes,
                    out var senderAgencyId))
            {
                RelayToSameAgencyClients<KerbalSrvMsg>(client, senderAgencyId, data);
            }
        }

        /// <summary>
        /// [Stage 6 / Phase 6.5] Pure helper that performs the per-agency disk
        /// write for a kerbal proto. Resolves the sender's agency, holds
        /// <see cref="AgencySystem.GetAgencyLock"/> across the
        /// <see cref="AgencySystem.Agencies"/> re-check + disk write to close
        /// the <c>/deleteagency</c> cascade race documented at Phase 6.3
        /// commit <c>90470f08</c> (CLAUDE.md Stack Notes "Phase 6.5 race-window").
        /// Writes via <see cref="FileHandler.WriteAtomic(string,byte[],int)"/>
        /// because each per-agency kerbal file is the ONLY copy of that
        /// agency's version of the kerbal — half-written files from a server
        /// kill are unacceptable.
        ///
        /// <para><b>Returns</b> <c>true</c> on successful write (caller relays);
        /// <c>false</c> on every DROP branch (caller skips relay).
        /// <paramref name="senderAgencyId"/> is set to <see cref="Guid.Empty"/>
        /// when the helper returns false on the mapping-miss branch; set to
        /// the resolved agency-id (then dropped) when the cascade-race branch
        /// fires.</para>
        ///
        /// <para><b>DROP semantics on resolve failure.</b> If
        /// <see cref="AgencySystem.AgencyByPlayerName"/> misses (torn registry
        /// — unreachable on a healthy handshake order) OR
        /// <see cref="AgencySystem.Agencies"/> miss under the lock (cascade
        /// raced our resolve), the proto is DROPPED with a Warning. Asymmetric
        /// with the read-side fallback-to-legacy in
        /// <see cref="ResolveKerbalsPathForRequester"/> — falling back to
        /// legacy on the write side would silently land mutations in a
        /// directory the per-agency handler never reads back (the gate=on
        /// equivalent of "writing to /dev/null"). The DROP makes the data loss
        /// audible.</para>
        ///
        /// <para>Pure-helper signature (string + bytes + numBytes, not
        /// <see cref="ClientStructure"/>) so ServerTest can directly exercise
        /// every branch without constructing a <see cref="Lidgren.Network.NetConnection"/>.
        /// Same testability pattern as Phase 6.4's
        /// <see cref="ResolveKerbalsPathForRequester"/>.</para>
        ///
        /// <para><b>Sender-vs-target tautology — verify before adding new callers.</b>
        /// The current contract has the target directory derived FROM the sender's
        /// agency, so "sender agency == target dir agency" is true by construction
        /// and the helper does not need a separate cross-agency-write check beyond
        /// the cascade-race guard. If a future Stage-6+ change introduces a path
        /// where the sender doesn't directly own the proto (e.g. a server-side
        /// kerbal extractor that pulls names out of an incoming vessel-proto and
        /// re-routes them to a different agency's roster — Phase 6.6+ /
        /// dual-channel EVA flow per spec §3), the tautology breaks and the helper
        /// MUST grow an explicit sender-vs-target agency check. Review SC-2.</para>
        ///
        /// <para><b>Phase 6.8 cross-subdir rename — lock-domain constraint.</b>
        /// Phase 6.8's <c>/setvesselagency</c> kerbal migration will move kerbal
        /// files between agency subdirs. The move MUST acquire BOTH
        /// <see cref="AgencySystem.GetAgencyLock"/> for source AND destination
        /// agencies (in deterministic Guid order to avoid AB-BA deadlock) before
        /// invoking the file rename, otherwise it races against concurrent calls
        /// to this helper on either side. Document at the call site when 6.8
        /// lands. Review C-1.</para>
        /// </summary>
        internal static bool TryWriteKerbalProtoPerAgency(string senderPlayerName, string kerbalName, byte[] data, int numBytes, out Guid senderAgencyId)
        {
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(senderPlayerName, out senderAgencyId))
            {
                LunaLog.Warning($"[fix:per-agency-kerbal-roster] DROPPED KerbalProto for {kerbalName} from {senderPlayerName}: no AgencyByPlayerName mapping under PerAgencyKerbalRosterEnabled. Either the registry is torn or the player has not yet auto-registered. The write is dropped (not legacy-fallback) because legacy is structurally unread under gate=on.");
                senderAgencyId = Guid.Empty;
                return false;
            }

            lock (AgencySystem.GetAgencyLock(senderAgencyId))
            {
                // Cascade-race guard. TryDeleteAgency holds this same lock
                // across Agencies.TryRemove + FolderDeleteRecursive — if we
                // see Agencies.ContainsKey=false after acquiring, the cascade
                // has either completed or is about to (we won the lock first
                // but the agency is gone). Drop rather than create a file in
                // a now-orphan subdir.
                if (!AgencySystem.Agencies.ContainsKey(senderAgencyId))
                {
                    LunaLog.Warning($"[fix:per-agency-kerbal-roster] DROPPED KerbalProto for {kerbalName} from {senderPlayerName}: agency {senderAgencyId:N} no longer in registry (TryDeleteAgency cascade raced the write). Agency was deleted between AgencyByPlayerName lookup and lock acquire.");
                    return false;
                }

                var path = Path.Combine(AgencySystem.GetKerbalsPathForAgency(senderAgencyId), $"{kerbalName}.txt");
                LunaLog.Debug($"[fix:per-agency-kerbal-roster] Saving kerbal {kerbalName} from {senderPlayerName} -> {path}");
                FileHandler.WriteAtomic(path, data, numBytes);
            }

            return true;
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

            if (AgencySystem.PerAgencyKerbalRosterEnabled)
            {
                HandleKerbalRemovePerAgency(client, message);
                return;
            }

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
            //
            // [Phase 6.5] Under PerAgencyKerbalRosterEnabled the K1 scan is
            // STRUCTURALLY MOOT — the Phase 6.4 request filter only delivers
            // same-agency kerbals to each client, so a client can never construct
            // a KerbalRemoveMsgData for a foreign-agency kerbal. The K1 scan is
            // therefore skipped under gate=on (the per-agency branch above takes
            // over). It runs unchanged under gate=off for the Stage 5 cohort. See
            // spec §Q-K1; Stage 7 cleanup pass removes both the scan and the
            // client-side ProtoCrewMember_Die Harmony patch once gate=on is the
            // default cohort.
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
        /// [Stage 6 / Phase 6.5] Per-agency write-path branch of
        /// <see cref="HandleKerbalRemove"/>. Delegates to the pure helper
        /// <see cref="TryDeleteKerbalPerAgency"/> and relays on success.
        /// K1 scan SKIPPED here (structurally moot — see comment in the
        /// gate=off branch).
        /// </summary>
        private static void HandleKerbalRemovePerAgency(ClientStructure client, KerbalRemoveMsgData message)
        {
            if (TryDeleteKerbalPerAgency(client.PlayerName, message.KerbalName, out var senderAgencyId))
            {
                RelayToSameAgencyClients<KerbalSrvMsg>(client, senderAgencyId, message);
            }
        }

        /// <summary>
        /// [Stage 6 / Phase 6.5] Pure helper that performs the per-agency disk
        /// delete for a kerbal remove. Mirrors
        /// <see cref="TryWriteKerbalProtoPerAgency"/>'s lock + ContainsKey shape.
        /// </summary>
        internal static bool TryDeleteKerbalPerAgency(string senderPlayerName, string kerbalName, out Guid senderAgencyId)
        {
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(senderPlayerName, out senderAgencyId))
            {
                LunaLog.Warning($"[fix:per-agency-kerbal-roster] DROPPED KerbalRemove for {kerbalName} from {senderPlayerName}: no AgencyByPlayerName mapping under PerAgencyKerbalRosterEnabled.");
                senderAgencyId = Guid.Empty;
                return false;
            }

            lock (AgencySystem.GetAgencyLock(senderAgencyId))
            {
                if (!AgencySystem.Agencies.ContainsKey(senderAgencyId))
                {
                    LunaLog.Warning($"[fix:per-agency-kerbal-roster] DROPPED KerbalRemove for {kerbalName} from {senderPlayerName}: agency {senderAgencyId:N} no longer in registry (TryDeleteAgency cascade raced the remove).");
                    return false;
                }

                var path = Path.Combine(AgencySystem.GetKerbalsPathForAgency(senderAgencyId), $"{kerbalName}.txt");
                LunaLog.Debug($"[fix:per-agency-kerbal-roster] Removing kerbal {kerbalName} from {senderPlayerName} -> {path}");
                FileHandler.FileDelete(path);
            }

            return true;
        }

        /// <summary>
        /// [Stage 6 / Phase 6.5] Per-agency-scoped relay. Sends <paramref name="data"/>
        /// to every <see cref="ServerContext.Clients"/> entry whose PlayerName
        /// maps to <paramref name="senderAgencyId"/> in
        /// <see cref="AgencySystem.AgencyByPlayerName"/>, EXCEPT the sender.
        /// Under the current 1:1 OwningPlayerName design this selects zero
        /// peers — sender's own client already has the canonical data, no
        /// other client in the agency exists. The structure is forward-
        /// compatible: if a future Stage-7+ commit introduces multi-player-
        /// per-agency, the filter selects exactly the right peers.
        ///
        /// <para>Inline-filter pattern (vs adding a new <see cref="MessageQueuer"/>
        /// method) matches the precedent of <see cref="Server.System.Agency.AgencySystemSender"/>'s
        /// per-target <c>SendToClient</c> loops — per-agency scoping is a
        /// caller concern, not a transport-layer concern.</para>
        /// </summary>
        private static void RelayToSameAgencyClients<TSrvMsg>(ClientStructure sender, Guid senderAgencyId, IMessageData data)
            where TSrvMsg : class, IServerMessageBase
        {
            // Sender is always in Clients, so Count <= 1 means there are zero
            // possible peers and the foreach below would be pure overhead. Under
            // the current 1:1 OwningPlayerName design this short-circuit fires
            // most of the time (every solo session); the loop only runs when at
            // least one other client is connected, at which point the
            // AgencyByPlayerName filter selects same-agency peers correctly.
            // Phase 6.5 review SS-2.
            if (ServerContext.Clients.Count <= 1)
                return;

            foreach (var otherClient in ServerContext.Clients.Values)
            {
                if (Equals(otherClient, sender))
                    continue;
                if (!AgencySystem.AgencyByPlayerName.TryGetValue(otherClient.PlayerName, out var peerAgencyId))
                    continue;
                if (peerAgencyId != senderAgencyId)
                    continue;
                MessageQueuer.SendToClient<TSrvMsg>(otherClient, data);
            }
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
