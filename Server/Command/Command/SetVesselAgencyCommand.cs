using LmpCommon.Locks;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Command.Command.Base;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Phase 3 Slice E-2 (MKS-compat Phase 3 closer). Admin command that
    /// REASSIGNS a single vessel from its current owning agency to a different
    /// one. Mutates <c>Vessel.OwningAgencyId</c> in the canonical store,
    /// migrates the per-router MKS partitions per pre-spec §4.e, releases the
    /// source owner's stale vessel-scoped locks, and broadcasts the ownership
    /// transition via <see cref="AgencyVisibilityMsgData"/>.
    ///
    /// <para><b>Why a NEW command, not extending <c>/transferagency</c>.</b>
    /// Stage 5.18d slice (e) <c>/transferagency</c> is an OWNER-RENAME — it
    /// preserves the agency's <c>AgencyId</c>, vessels keep their
    /// <c>lmpOwningAgency</c> stamp, only the player handle attached to the
    /// agency changes. The vessel-level A→B substantive reassignment surface
    /// is THIS command (operator confirmed session 30 / 2026-05-18). The
    /// pre-spec §4.e migration logic (per-router policies, dual-lock ordering,
    /// wire echo shape) is preserved on this command — only the command-name
    /// attachment shifted.</para>
    ///
    /// <para><b>9-step orchestration</b> (matches the load-bearing contract
    /// documented in <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>
    /// XML, replicated here for the operator-command reader):</para>
    /// <list type="number">
    ///   <item><b>Same-stamp short-circuit BEFORE acquiring locks.</b>
    ///         No-op log; no helpers, no broadcast, no SaveAgency. Returns
    ///         true (success-no-op).</item>
    ///   <item><b>Acquire dual locks in <see cref="Guid.CompareTo"/> order.</b>
    ///         Lower-comparing AgencyId first. BUG-033 precedent. When the
    ///         source is Unassigned (vessel.OwningAgencyId == Guid.Empty)
    ///         there is no source AgencyState — collapse to single-lock on
    ///         destination only.</item>
    ///   <item><b>Mutate <c>vessel.OwningAgencyId = destination.AgencyId</c>
    ///         BEFORE calling migration helpers.</b> Post-migration the
    ///         5.17a cross-agency rejection treats destination as
    ///         authoritative immediately.</item>
    ///   <item><b>Call the four per-router helpers</b> (skip when source
    ///         is null — Unassigned-source has no per-router entries by
    ///         construction; the routers' first-served path puts entries
    ///         under the CURRENT writer's agency, never under an "Empty
    ///         vessel agency"):
    ///         <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>,
    ///         <see cref="AgencyOrbitalRouter.MigrateForVesselTransfer"/>,
    ///         <see cref="AgencyPlanetaryRouter.InspectAffectedEntriesForVesselTransfer"/>
    ///         (read-only, Q2 NO-MIGRATE),
    ///         <see cref="AgencyScanRouter.MigrateForVesselTransfer"/>
    ///         (Mod-compat S2 Decision §3 — vessel-keyed scanner records
    ///         follow the vessel; per-body Coverage stays put).</item>
    ///   <item><b>Persist BOTH agencies</b> (only destination when source
    ///         is null/orphaned).
    ///         <see cref="AgencySystem.SaveAgency"/> before releasing the
    ///         agency locks. Pre-spec §4.e line 567 invariant — without
    ///         this pair a crash before the next periodic
    ///         <see cref="BackupSystem.RunBackup"/> silently loses the
    ///         migration.</item>
    ///   <item><b><see cref="BackupSystem.RunBackup"/> AFTER the SaveAgency
    ///         pair</b> to flush <c>vessel.OwningAgencyId</c> to the
    ///         universe vessel file. Without this the vessel.cfg keeps the
    ///         OLD agency stamp until the next periodic backup; a crash in
    ///         that window leaves vessel.cfg disagreeing with
    ///         AgencyState.txt → cross-agency-reject loop on reconnect.
    ///         Same call shape as <see cref="DeleteAgencyCommand"/>.</item>
    ///   <item><b>Release the source owner's stale vessel-scoped locks</b>
    ///         (Control / Update / UnloadedUpdate) on the moved vessel.
    ///         The 5.17a guard rejects NEW acquires but existing held
    ///         locks remain — the source owner's KSP keeps emitting vessel
    ///         messages that the soak Finding-2 write-path guard silently
    ///         drops → vessel freezes from her perspective. Mirror
    ///         <see cref="DeleteAgencyCommand"/>'s
    ///         <c>ReleaseOldOwnerLocksOnDemotedVessels</c> filtered by
    ///         <c>lock.VesselId == movedVesselId</c> rather than the
    ///         demoted-vessel set. For Unassigned-source, release ALL
    ///         vessel-scoped locks on this vessel held by players NOT in
    ///         the destination agency (anyone could have grabbed a lock
    ///         under the spec §10 Q3 Unassigned bypass; we don't have a
    ///         single old-owner handle to filter by).</item>
    ///   <item><b>Emit wire messages in ORDER</b>: (a)
    ///         <see cref="AgencySystemSender.BroadcastVisibilityChange"/>
    ///         with the V→destAgencyId entry FIRST, then (b) source-owner
    ///         removal echoes (kolony + orbital), then (c)
    ///         destination-owner add echoes (kolony + orbital). Channel
    ///         22 is ReliableOrdered per-recipient so emit order = apply
    ///         order client-side. Pre-Visibility echoes would briefly
    ///         render "I still own V but its entries are gone" on the
    ///         source owner's client.</item>
    ///   <item><b>Release the dual lock.</b> (Handled implicitly by
    ///         the nested <c>lock</c> statements unwinding.)</item>
    /// </list>
    ///
    /// <para><b>Reversibility.</b> Re-run with the original agency token to
    /// undo. No <c>--confirm</c> flag — the operation is reversible and the
    /// state mutation is bounded (one vessel + the agency's per-router
    /// partitions referencing that vessel).</para>
    ///
    /// <para><b>Connected-source-owner handling.</b> If the source owner is
    /// currently connected, their client's view briefly mismatches:
    /// vessel.OwningAgencyId is now destination on the server but their
    /// VesselSync cache still says source. The next VesselSync round-trip
    /// + the AgencyVisibilityMsgData broadcast both correct this. The
    /// source-owner removal echoes (kolony + orbital) prune their per-agency
    /// state caches for V. Operators wanting an instant client-side
    /// convergence can <c>/kick</c> the source owner; on reconnect they get
    /// fresh per-agency catch-up.</para>
    ///
    /// <para><b>Connected-owner kerbal-roster race (Phase 6.8
    /// integration-lens v1 SHOULD FIX S1+S2).</b> Under combined gate=on
    /// the kerbal-migration step takes the dual agency lock — concurrent
    /// <c>HandleKerbalProto</c> from EITHER source or destination owner
    /// blocks on <c>GetAgencyLock</c> until we release. Once released two
    /// race outcomes are possible:
    /// <list type="bullet">
    ///   <item><b>Source-owner KerbalProto resurrection:</b> source owner's
    ///         queued KerbalProto for the moved kerbal lands AFTER our move,
    ///         re-writing the file in source's subdir. <c>TryWriteKerbalProtoPerAgency</c>
    ///         is by-construction tautological (sender = source-owner =
    ///         source-agency-target) so the cross-agency-write guard never
    ///         fires. Outcome: kerbal file in BOTH source and destination
    ///         subdirs; destination still has the operator-intended
    ///         migrated copy; source has a "stale resurrected" copy.</item>
    ///   <item><b>Destination-owner KerbalProto overwrite:</b> destination
    ///         owner's queued KerbalProto for a same-name kerbal (one they
    ///         had pre-existing, e.g. their own auto-registered Jeb) lands
    ///         AFTER our move, silently overwriting the bytes we just
    ///         migrated (Phase 6.5's <c>TryWriteKerbalProtoPerAgency</c> at
    ///         <c>KerbalSystem.cs:159</c> just calls
    ///         <c>FileHandler.WriteAtomic</c> without checking for a
    ///         destination-side existing same-name file). Outcome: Alice's
    ///         migrated Jeb (level 5, 100 flights) replaced by Bob's
    ///         pre-existing Jeb (level 1, 0 flights). Lock-free pre-check
    ///         + under-lock re-check both detect this case and REFUSE the
    ///         whole command — but only if Bob's file existed BEFORE our
    ///         pre-check. If Bob's KerbalProto lands AFTER pre-check + AFTER
    ///         dual-lock-release the under-lock re-check passes and the
    ///         clobber happens silently.</item>
    /// </list>
    /// Both races are operator-mitigated via <c>/kick</c> BOTH source and
    /// destination owners BEFORE the command. Same posture as the existing
    /// in-flight KolonyEntry race documented in the postfix-race note at
    /// step 4 below. Documented + accepted; structurally closing these
    /// races would require holding agency locks across an arbitrarily-long
    /// operator-driven window, trading the race for a much larger
    /// denial-of-service surface.</para>
    ///
    /// <para><b>Phase 4 Slice F — WOLF entities are NOT vessel-keyed; this
    /// command is a NO-OP for the 5 WOLF dicts.</b>
    /// <list type="bullet">
    ///   <item><b>WolfDepots / WolfRoutes / WolfHoppers / WolfTerminals</b>
    ///         are body+biome keyed (<c>$"{Body}|{Biome}"</c>) or
    ///         metadata-Guid keyed (HopperMetadata.Id / TerminalMetadata.Id).
    ///         None of them reference a vessel id; reassigning a vessel from
    ///         agency A to agency B leaves these dicts entirely untouched in
    ///         both agencies.</item>
    ///   <item><b>WolfCrewRoutes</b> is Guid-keyed by
    ///         <c>CrewRoute.UniqueId</c>; passengers are referenced by kerbal
    ///         name, not by the kerbal's currently-aboard vessel id. The
    ///         non-obvious corollary: <c>/setvesselagency</c> on a vessel
    ///         currently carrying a kerbal who is also enrolled in an Enroute
    ///         CrewRoute is ALSO a NO-OP for the CrewRoute record — the
    ///         passenger list is fixed at Launch time per WOLF source
    ///         contract. Pre-Launch (FlightStatus == Boarding) the list is
    ///         mutable: <c>CrewRoute.cs:141-184</c> Embark adds passengers
    ///         and Disembark removes them. Post-Launch (Enroute / Arrived)
    ///         the list is immutable until <c>Disembark</c> requires
    ///         <c>FlightStatus == Arrived</c> AND operator click, OR the
    ///         Slice F cascade restores passengers on <c>/deleteagency</c>.
    ///         Vessel-reassignment never moves the kerbal between agencies'
    ///         CrewRoute partitions regardless of FlightStatus.</item>
    /// </list>
    /// Operators who want to migrate WOLF state from one agency to another
    /// would need to hand-edit <c>Universe/Agencies/{guid}.txt</c>; there is
    /// no in-band command for that today (and no design surface for one —
    /// WOLF entities are per-agency by design, not per-vessel). <b>Do NOT add
    /// a WOLF migration walk here</b> — the dicts have no FK to the moved
    /// vessel and a migration walk would produce no observable change.</para>
    ///
    /// <para><b>Phase 6.8 — vessel-crew kerbal files DO migrate.</b>
    /// Consumer-lens v1 CONSIDER C4 — distinct from the WOLF NO-OP block
    /// above. Under combined gate=on
    /// (<see cref="AgencySystem.PerAgencyKerbalRosterEnabled"/>=true) the
    /// kerbals aboard the moved vessel have their per-agency kerbal files
    /// moved between
    /// <c>Universe/Agencies/{source:N}/Kerbals/{name}.txt</c> and
    /// <c>Universe/Agencies/{dest:N}/Kerbals/{name}.txt</c>. The migration
    /// step runs inside the existing
    /// <see cref="RunUnderLockOrder(AgencyState,AgencyState,Action)"/>
    /// critical section between the per-router migration helpers and the
    /// <see cref="AgencySystem.SaveAgency(Guid)"/> pair. Same-name kerbal in
    /// destination's subdir refuses the WHOLE command cleanly (no vessel
    /// mutation, no router migration, no SaveAgency, no wire emit). Empty
    /// vessels (no <c>crew = NAME</c> lines) skip the step entirely. Gate=off
    /// keeps the historical NO-OP-for-crew posture bit-for-bit. The
    /// <see cref="ExtractCrewFromVessel"/> +
    /// <see cref="CheckDestinationCollisions"/> +
    /// <see cref="ResolveKerbalSourcePathForMove"/> helpers carry the
    /// per-kerbal logic.</para>
    ///
    /// <para><b>Cross-command race with <c>/deleteagency</c></b> (Phase 6.8
    /// integration-lens v1 MUST FIX M1 + CONSIDER C3). A concurrent admin
    /// running <c>/deleteagency destAgency --confirm</c> on a second terminal
    /// can interleave with this command. Both serialize on
    /// <c>GetAgencyLock(destAgency)</c>. If <c>/setvesselagency</c> wins:
    /// vessel + kerbals migrate to destination, then <c>/deleteagency</c>
    /// cascade deletes destination's <c>Kerbals/</c> subdir (taking the
    /// freshly-migrated kerbals with it); the vessel is now stamped to a
    /// deleted agency-id. Phase 6.7's <c>--restore-to</c> destination flag
    /// can re-rescue these kerbals. If <c>/deleteagency</c> wins: the post-
    /// dual-lock cascade-race re-check at step 2c above bails with an Error;
    /// no mutation lands. Operator discipline (single-admin sessions / serialise
    /// admin commands) is the cheapest mitigation; the step-2c bail is the
    /// safety net.</para>
    /// </summary>
    public class SetVesselAgencyCommand : SimpleCommand
    {
        private static readonly HashSet<LockType> VesselScopedLockTypes = new HashSet<LockType>
        {
            LockType.Control,
            LockType.Update,
            LockType.UnloadedUpdate,
        };

        /// <summary>
        /// [Stage 6 Phase 6.9-hardening — security-lens MUST FIX #3] Maximum
        /// number of crew this command will migrate on a single invocation.
        /// Prevents a malformed or hostile vessel proto with thousands of
        /// forged <c>crew = NAME{i}</c> lines from locking the dual agency
        /// lock for minutes while iterating ReadFile + WriteAtomic + FileDelete
        /// per name. Legitimate KSP-stock vessels carry ≤8 crew per part; the
        /// most generously-modded large stations top out around ~50 crew.
        /// 64 is a generous cap above any realistic crewed-vessel value. Set
        /// as <c>public const</c> so tests can pin the threshold.
        /// </summary>
        public const int MaxCrewMigration = 64;

        public override bool Execute(string commandArgs)
        {
            // (1) Pure parse.
            if (!SetVesselAgencyCommandParser.TryParse(commandArgs, out var vesselToken, out var agencyToken, out var parseError))
            {
                LunaLog.Error(parseError);
                LunaLog.Normal(SetVesselAgencyCommandParser.UsageBanner);
                return false;
            }

            // (2) Gate refusal — same shape as /setagency + /transferagency +
            // /deleteagency. Both off-states refuse loudly with the resolution
            // path inlined.
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
            {
                LunaLog.Error(
                    "setvesselagency: requires PerAgencyCareer=true. Under PerAgencyCareer=false vessels carry " +
                    "no agency stamp; there is no per-vessel ownership to reassign.");
                return false;
            }
            if (!AgencySystem.PerAgencyEnabled)
            {
                LunaLog.Error(
                    "setvesselagency: requires GameMode=Career. PerAgencyCareer=true but GameMode is not Career — set " +
                    "GameMode=Career in Settings/GeneralSettings.xml to activate, or set PerAgencyCareer=false in " +
                    "Settings/GameplaySettings.xml to disable per-agency cleanly (may flip GameDifficulty to Custom " +
                    "— see CLAUDE.md Settings caveat).");
                return false;
            }

            // (3) Resolve vessel guid. Accept both "N" (32 hex) and "D" (with hyphens)
            // forms — Guid.TryParse handles both. Normalise to canonical Guid for
            // VesselStoreSystem lookup (the dict key is Guid not string).
            if (!Guid.TryParse(vesselToken, out var movedVesselId))
            {
                LunaLog.Error(
                    $"setvesselagency: vessel-guid '{vesselToken}' is not a parseable Guid. Accept either " +
                    "the 32-hex \"N\" form (matches lmpOwningAgency on-disk shape) or the hyphenated \"D\" form " +
                    "(matches /listclients output). Run /vessel info to confirm vessel guids on this server.");
                return false;
            }
            if (movedVesselId == Guid.Empty)
            {
                LunaLog.Error(
                    "setvesselagency: vessel-guid must be non-empty. Guid.Empty is the Unassigned-vessel sentinel " +
                    "(spec §10 Q3), not a real vessel identity.");
                return false;
            }
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(movedVesselId, out var vessel))
            {
                LunaLog.Error(
                    $"setvesselagency: no vessel with guid {movedVesselId:N} is present in the universe store. " +
                    "Vessel guids change when KSP regenerates a vessel (e.g. recovery + relaunch); run /vessel info " +
                    "to confirm the current guid.");
                return false;
            }

            // (4) Resolve destination agency token.
            if (!AgencySystem.TryResolveAgencyToken(agencyToken, out var destination))
            {
                if (AgencySystem.Agencies.IsEmpty)
                {
                    LunaLog.Error(
                        "setvesselagency: no agencies are registered yet. An agency mints on the owning player's " +
                        "first connect under PerAgencyCareer=true. /listagencies confirms the current registry state.");
                }
                else
                {
                    LunaLog.Error(
                        $"setvesselagency: agency token '{agencyToken}' does not match any registered agency. " +
                        "Pass either an agency id (run /listagencies) or the agency owner's REGISTRATION-time " +
                        "LMP handle. Orphaned agency ids from boot warnings have no in-memory AgencyState; recover " +
                        "by restoring Universe/Agencies/{guid}.txt(.bak), or accept the loss and let owning players " +
                        "mint fresh agencies on reconnect.");
                }
                return false;
            }

            // Snapshot pre-mutation source identity. Source AgencyState may be:
            //  a) Non-null + registered — typical A→B
            //  b) Null because vessel.OwningAgencyId == Guid.Empty (Unassigned)
            //  c) Null because the id is non-Empty but orphaned (AgencyState file
            //     missing — caught by boot warning). Treated same as (b) below.
            var sourceAgencyId = vessel.OwningAgencyId;
            AgencySystem.Agencies.TryGetValue(sourceAgencyId, out var source);
            var sourceIsUnassigned = sourceAgencyId == Guid.Empty;
            var sourceIsOrphaned = !sourceIsUnassigned && source == null;
            if (sourceIsOrphaned)
            {
                // Round-1 upgrade-lens SHOULD FIX S2 — orphan upgrade gets BOTH
                // a Warning (operator may want to investigate why an agency
                // file went missing) and a Normal-level line (so the
                // (verb, vessel-id, key=value) GUI grep picks up the
                // operator-action in the same stream as the regular summary).
                LunaLog.Warning(
                    $"[fix:per-agency-career] setvesselagency vessel={movedVesselId:N} has orphaned source agency " +
                    $"{sourceAgencyId:N} (stamp present, AgencyState absent). Proceeding without source-side " +
                    "migration — there are no per-router partitions to move FROM. The destination stamp + " +
                    "visibility broadcast + lock release still apply.");
                LunaLog.Normal(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} upgrade-from-orphan " +
                    $"source-id={sourceAgencyId:N} dest={destination.AgencyId:N}");
            }

            var sourceOwnerName = source?.OwningPlayerName ?? string.Empty;
            var destAgencyId = destination.AgencyId;
            var destOwnerName = destination.OwningPlayerName ?? string.Empty;

            // Step 1: Same-stamp short-circuit BEFORE acquiring locks. The
            // ReferenceEquals guards inside the migration helpers are
            // defense-in-depth backstops — DO NOT rely on them for the
            // operator-visible no-op log.
            if (sourceAgencyId == destAgencyId)
            {
                // Round-1 consumer-lens MUST FIX — result=noop key lets a GUI
                // launcher's regex match `result=(\w+)` cleanly for "did
                // anything change?" without substring-matching `no-op` text.
                LunaLog.Normal(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} result=noop " +
                    $"dest={destAgencyId:N} dest-owner='{destOwnerName}' " +
                    "(vessel already assigned to this agency)");
                return true;
            }

            // [Stage 6 / Phase 6.8] Pre-flight crew extraction + lock-free
            // collision pre-check. Runs ONLY under combined gate=on; gate=off
            // keeps the historical NO-OP-for-crew posture (vessel stamp moves,
            // kerbal files stay put).
            //
            // The pre-check happens BEFORE RunUnderLockOrder so a doomed
            // command (destination has same-name kerbal) refuses CLEANLY
            // without taking dual locks, without router migration, without
            // vessel-field mutation. Race window between pre-check and the
            // under-lock re-check (step 4b below) is closed by the optimistic
            // re-check at write time — destination owner could write a same-
            // name kerbal in that window, in which case THAT kerbal DROPs from
            // the batch but the rest of the move + command proceeds.
            //
            // Empty crew list (no `crew = NAME` lines in the vessel proto) is
            // the dominant case — most vessels are unmanned (probes, satellites,
            // landers). The kerbal-move loop runs zero times and is invisible
            // to the operator.
            var migrateKerbals = AgencySystem.PerAgencyKerbalRosterEnabled;
            List<string> crewNames = null;
            if (migrateKerbals)
            {
                if (!ExtractCrewFromVessel(movedVesselId, out crewNames))
                {
                    // Defensive: serialization failure on the vessel text
                    // (caught by ExtractCrewFromVessel's try/catch). Vessel is
                    // valid enough to be in CurrentVessels but its ToString
                    // threw — log Warning and continue with empty crew rather
                    // than fail the whole command. The vessel field mutation +
                    // router migration are still legitimate; kerbal files just
                    // don't migrate. Operator sees the Warning and can
                    // hand-move kerbal files post-hoc if needed.
                    LunaLog.Warning(
                        $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} crew-extraction failed; " +
                        "continuing with no kerbal migration (vessel-stamp + router migration still apply).");
                    crewNames = new List<string>();
                }

                // [Stage 6 Phase 6.9-hardening — security-lens MUST FIX #3]
                // Crew-count cap to prevent dual-agency-lock-held DoS via a
                // vessel proto with thousands of forged `crew = NAME{i}`
                // lines. KSP-stock vessels carry ≤8 crew per part; the most
                // generously-modded large stations top out around ~50 crew.
                // 64 caps the migration loop's worst-case lock-held disk
                // I/O while leaving generous headroom for legitimate KSP
                // vessels. Refuses the whole command on overflow because
                // splitting the migration across multiple lock-release-
                // and-reacquire cycles would lose the optimistic-collision
                // re-check authoritativeness (see Phase 6.8 step-4b XML at
                // SetVesselAgencyCommand.cs).
                if (crewNames.Count > MaxCrewMigration)
                {
                    LunaLog.Error(
                        $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} result=refused-crew-overflow " +
                        $"vessel proto reports {crewNames.Count} crew which exceeds the cap of {MaxCrewMigration}. " +
                        "This is far above any legitimate KSP-stock or supported-mod vessel crew count and likely " +
                        "indicates a malformed or hostile vessel proto. The vessel stamp + router migration are " +
                        "NOT applied. Inspect Universe/Vessels/{vid}.txt for the actual crew block before re-running.");
                    return false;
                }

                if (crewNames.Count > 0)
                {
                    var collisions = CheckDestinationCollisions(crewNames, destAgencyId);
                    if (collisions.Count > 0)
                    {
                        // [Phase 6.8 consumer-lens v1 SHOULD FIX S1] Resolution
                        // text only lists actions that actually move the
                        // collision: rename via AC (operator-side data
                        // mutation) or pick a different destination. The
                        // earlier draft suggested /kick destination owner as
                        // a third option, but the collision check is a pure
                        // FileHandler.FileExists call against on-disk state —
                        // kicking the owner offline doesn't make the file
                        // vanish, so the second invocation would refuse
                        // identically. Removed.
                        LunaLog.Error(
                            $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} result=refused-collision " +
                            $"destination agency {destAgencyId:N} already has {collisions.Count} same-name " +
                            $"kerbal(s) aboard the moved vessel: [{string.Join(", ", collisions)}]. " +
                            "Resolution options: (a) rename destination's conflicting kerbal(s) via the " +
                            "Astronaut Complex BEFORE re-running this command — destination owner sees the " +
                            $"rename, server's KerbalProto routing under PerAgencyKerbalRoster=on records the new " +
                            $"name in {destOwnerName}'s subdir, and the colliding name slot frees up; OR " +
                            "(b) pick a different destination agency (/listagencies for the registry); OR " +
                            "(c) hand-delete the conflicting file from " +
                            $"Universe/Agencies/{destAgencyId:N}/Kerbals/ while the server is offline. " +
                            "The vessel stamp + router migration are NOT applied; re-run after resolving.");
                        return false;
                    }
                }
            }

            // Step 2: Acquire dual locks in Guid.CompareTo order. When source
            // is null (Unassigned or orphaned), collapse to single-lock on
            // destination only — there's no source partition to read.
            //
            // Step 3-5 happen INSIDE the locks:
            //   * mutate vessel.OwningAgencyId = destAgencyId
            //   * call three per-router helpers (skip when source is null)
            //   * [Phase 6.8] per-kerbal file move between agency subdirs
            //   * SaveAgency(destination) (always)
            //   * SaveAgency(source.AgencyId) (when source non-null)
            //
            // The dual-lock critical section is held across both SaveAgency
            // calls; the per-agency disk-flush cost is documented in
            // AgencyKolonyRouter.MigrateForVesselTransfer XML.
            KolonyMigrationResult kolonyResult = null;
            OrbitalMigrationResult orbitalResult = null;
            PlanetaryInspectionResult planetaryResult = null;
            ScanMigrationResult scanResult = null;
            // [Phase 6.8] Captured kerbal bytes for the wire push step. Keyed
            // by kerbal name. Populated under the dual lock, consumed AFTER
            // lock release (wire push is outside the critical section to keep
            // the lock window short — same shape as the existing per-router
            // echo emit at lines 467-497 below).
            var movedKerbalBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var droppedKerbalNames = new List<string>();
            // [Phase 6.8 upgrade-lens v1 SHOULD FIX Finding 4] Count of
            // kerbals rescued via Tier-2 legacy fallback in
            // ResolveKerbalSourcePathForMove. Distinguishes "the operator
            // ran on a v0-v7 upgrade universe and we rescued legacy-stranded
            // kerbals" from "the kerbal files were in their healthy
            // per-agency location" in the summary line.
            var kerbalsLegacyStranded = 0;

            // Track whether we actually performed a mutation under the lock.
            // The post-lock re-check (Round-1 integration-logic CONSIDER C1)
            // can short-circuit a concurrent second operator's call after the
            // first one already landed. False here means "no mutation, no
            // wire emit, no audit lines" — same observable shape as the
            // step-1 same-stamp short-circuit but logged distinctly.
            var mutated = false;

            RunUnderLockOrder(source, destination, () =>
            {
                // Step 2b (Round-1 integration-logic CONSIDER C1): post-lock
                // re-check for concurrent-operator races. Two concurrent
                // operators on the same vessel both pass step 1, then
                // serialize on the dual lock. The first mutates; the second
                // enters here with the stamp already updated — short-circuit
                // so we don't run an empty migration, burn two SaveAgency
                // disk flushes, and emit a duplicate Visibility broadcast.
                if (vessel.OwningAgencyId == destAgencyId)
                    return;

                // Step 2c (Phase 6.8 integration-lens MUST FIX M1): post-lock
                // cascade-race re-check. A concurrent /deleteagency could
                // have removed destination (or source) from Agencies between
                // our token resolve at line 217 and the dual-lock acquire.
                // Without this check we'd:
                //   - stamp the vessel to a deleted agency-id
                //   - run router migration helpers against a now-orphaned
                //     AgencyState reference (the destination variable
                //     captured pre-lock at line 217 is now unreachable from
                //     Agencies)
                //   - call SaveAgency(destAgencyId) which silently misses
                //     because Agencies no longer has the key
                //   - broadcast AgencyVisibilityChange pointing the vessel
                //     at a deleted agency — peer clients render "agency
                //     missing" forever
                // Same defensive pattern as Phase 6.5's
                // TryWriteKerbalProtoPerAgency at KerbalSystem.cs:147. Bail
                // by setting `mutated = false` so the post-lock summary line
                // routes through the raced-no-op log path and the operator
                // sees a distinct diagnostic.
                if (!AgencySystem.Agencies.ContainsKey(destAgencyId)
                    || (source != null && !AgencySystem.Agencies.ContainsKey(source.AgencyId)))
                {
                    LunaLog.Error(
                        $"[fix:per-agency-career] setvesselagency {movedVesselId:N} ABORTED under dual-lock: " +
                        $"a concurrent /deleteagency removed destination (Agencies.ContainsKey={AgencySystem.Agencies.ContainsKey(destAgencyId)}) " +
                        $"or source (Agencies.ContainsKey={(source == null ? "no-source-state" : AgencySystem.Agencies.ContainsKey(source.AgencyId).ToString())}) " +
                        "between token-resolve and dual-lock acquire. Vessel stamp UNCHANGED; no router migration; " +
                        "no kerbal migration; no wire emit. Re-run with /listagencies fresh state.");
                    return;
                }

                // Step 3: Mutate vessel.OwningAgencyId BEFORE calling migration
                // helpers. Post-mutation the 5.17a cross-agency rejection treats
                // destination as authoritative immediately, so the migration
                // helpers (which iterate sourceState's per-router dicts) cannot
                // race against a concurrent same-agency router upsert that would
                // re-claim the moved vessel — the router's check at line 146
                // sees the new stamp.
                //
                // [Round-1 general-lens MUST FIX #1] The vessel field write
                // MUST be wrapped in the per-vessel
                // <see cref="VesselDataUpdater.GetVesselLock"/> object — the
                // dual agency lock held by RunUnderLockOrder gates per-router
                // dicts, NOT vessel fields. The proto-ingest path at
                // VesselDataUpdater.cs:88 holds this same lock around its
                // existingStored read + auth-preserve write (S4 retro-review
                // precedent). Without the lock here, a concurrent proto-
                // ingest could read OwningAgencyId pre-mutation, run the
                // sticky-existing branch (line 134), and silently revert our
                // mutation back to source. The 5.17a guard's reads from
                // receive threads see whatever Fields.Update wrote last.
                lock (VesselDataUpdater.GetVesselLock(movedVesselId))
                {
                    vessel.OwningAgencyId = destAgencyId;
                }
                mutated = true;

                // Step 4: Call all three per-router helpers (skip when source
                // is null — Unassigned-source has no entries to migrate FROM
                // by construction).
                //
                // [Round-1 integration-logic MUST FIX #2 — POSTFIX-RACE NOTE]
                // A kolony/orbital/planetary postfix message from the source
                // owner can arrive on a network thread between our step-3 stamp
                // and the router's TryRoute entering its own per-agency lock.
                // That postfix's router will block on AgencySystem.GetAgencyLock
                // (held by us via RunUnderLockOrder). When the postfix unblocks
                // post-command, the router reads vessel.OwningAgencyId = destAgencyId,
                // sees the sender's agency (source's owner = aliceAgency), and
                // rejects with a Warning log line. The source owner's
                // in-flight research contribution silently vanishes from her
                // perspective — there is no client-visible diagnostic. Race
                // window is bounded by the dual-lock critical section duration.
                // Fixing this race structurally requires holding source's lock
                // across the entire command for an arbitrarily long window
                // (operator-driven), which trades a small race for a much
                // larger denial-of-service surface. Documented + accepted;
                // operators should /kick the source owner BEFORE
                // setvesselagency to fully close it.
                if (source != null)
                {
                    kolonyResult = AgencyKolonyRouter.MigrateForVesselTransfer(source, destination, movedVesselId);
                    orbitalResult = AgencyOrbitalRouter.MigrateForVesselTransfer(source, destination, movedVesselId);
                    planetaryResult = AgencyPlanetaryRouter.InspectAffectedEntriesForVesselTransfer(source, movedVesselId);
                    // [Mod-compat S2 / Decision §3 + D3] Vessel-keyed scanner
                    // records follow the vessel A→B; per-body Coverage stays
                    // put (A's discoveries of Eve stay A's). Mirrors the
                    // kolony migrate-with-vessel pattern; ScanMigrationResult
                    // is single-entry (vessel-keyed, at most one record).
                    scanResult = AgencyScanRouter.MigrateForVesselTransfer(source, destination, movedVesselId);
                }

                // Step 4b: [Phase 6.8] Per-kerbal file move between agency
                // subdirs under combined gate=on. Slotted between router
                // migration (step 4) and SaveAgency (step 5) so that:
                //   - The cascade-race re-check below uses the same lock state
                //     the rest of the critical section runs under.
                //   - The optimistic-collision re-check catches a same-name
                //     write that landed in destination between the pre-check
                //     (lock-free) and now.
                //   - SaveAgency at step 5 doesn't include kerbal-file paths
                //     in any per-agency lock-flushed disk state, so kerbal
                //     files writing first then SaveAgency writing after is
                //     correct ordering.
                //   - [Phase 6.8 consumer-lens v1 CONSIDER C1] WHY NOT AFTER
                //     BOTH SaveAgency CALLS? The dual lock unwinds when the
                //     action lambda returns. Moving kerbal files AFTER lock
                //     release would break the optimistic-collision re-check's
                //     authoritativeness — TryWriteKerbalProtoPerAgency
                //     (KerbalSystem.cs:139) acquires the SAME dest agency
                //     lock we hold here, so under-lock our re-check is final.
                //     Post-lock the re-check is racy against concurrent
                //     destination-owner KerbalProto writes. Keep the move
                //     INSIDE the lock window.
                if (migrateKerbals && crewNames.Count > 0)
                {
                    // Cascade-race re-check: destination agency could have
                    // been deleted between pre-check and dual-lock acquire.
                    // Source agency is checked too when non-null. Either miss
                    // DROPs the WHOLE kerbal-migration step (with a single
                    // Warning) but continues the rest of the command — the
                    // vessel stamp + router migration are already in place
                    // and we still need to SaveAgency + emit Visibility +
                    // release locks. Operator sees the inconsistency in the
                    // summary line.
                    var destCascaded = !AgencySystem.Agencies.ContainsKey(destAgencyId);
                    var sourceCascaded = source != null && !AgencySystem.Agencies.ContainsKey(source.AgencyId);
                    if (destCascaded || sourceCascaded)
                    {
                        LunaLog.Warning(
                            $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} DROPPED kerbal " +
                            $"migration: cascade-race detected ({(destCascaded ? "destination" : "source")} " +
                            "agency removed between pre-check and dual-lock acquire). Vessel stamp + router " +
                            "migration still apply; kerbal files NOT moved. Manual recovery: re-run command " +
                            "after the racing /deleteagency completes.");
                    }
                    else
                    {
                        var destKerbalsDir = AgencySystem.GetKerbalsPathForAgency(destAgencyId);
                        foreach (var name in crewNames)
                        {
                            try
                            {
                                var srcPath = ResolveKerbalSourcePathForMove(name, sourceAgencyId, sourceIsUnassigned || sourceIsOrphaned, out var resolvedViaLegacy);
                                if (resolvedViaLegacy)
                                    kerbalsLegacyStranded++;
                                if (string.IsNullOrEmpty(srcPath) || !FileHandler.FileExists(srcPath))
                                {
                                    LunaLog.Warning(
                                        $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} " +
                                        $"kerbal '{name}' has no file at any expected path (per-agency or legacy); " +
                                        "skipping. Vessel proto still references this name; if the kerbal exists " +
                                        "only in the vessel ConfigNode (no on-disk file), no state migration is " +
                                        "needed.");
                                    droppedKerbalNames.Add(name);
                                    continue;
                                }

                                var destPath = Path.Combine(destKerbalsDir, name + ".txt");

                                // Optimistic-collision re-check under the
                                // lock. Between pre-check and now, dest owner
                                // could have written a same-name kerbal via
                                // HandleKerbalProto. Under combined gate=on,
                                // TryWriteKerbalProtoPerAgency holds the
                                // SAME dest-agency lock we're inside right
                                // now, so this re-check IS authoritative —
                                // no concurrent write can land between this
                                // FileExists and the WriteAtomic below.
                                if (FileHandler.FileExists(destPath))
                                {
                                    LunaLog.Error(
                                        $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} " +
                                        $"kerbal '{name}' DROPPED from migration: destination acquired a same-" +
                                        "name kerbal between pre-check and under-lock re-check. Source file " +
                                        "preserved; destination file preserved. Operator may resolve via " +
                                        "rename + re-run.");
                                    droppedKerbalNames.Add(name);
                                    continue;
                                }

                                // Ensure destination parent directory exists.
                                // Defensive against operator-deleted subdirs;
                                // Phase 6.3 lifecycle hook normally seeds it
                                // at agency mint.
                                if (!FileHandler.FolderExists(destKerbalsDir))
                                    FileHandler.FolderCreate(destKerbalsDir);

                                // Read-then-write-then-delete order: a crash
                                // between WriteAtomic and FileDelete leaves
                                // the file in BOTH dirs (recoverable: operator
                                // re-runs, the pre-check + under-lock re-check
                                // detect the dest file and DROP that kerbal;
                                // operator hand-deletes the stale source).
                                // Reverse order would lose the kerbal on a
                                // mid-batch crash. Same posture as
                                // SaveAgency(dest) → SaveAgency(source) below.
                                var bytes = FileHandler.ReadFile(srcPath);
                                FileHandler.WriteAtomic(destPath, bytes, bytes.Length);
                                FileHandler.FileDelete(srcPath);

                                movedKerbalBytes[name] = bytes;
                            }
                            catch (Exception ex)
                            {
                                // Per-kerbal isolation. A filesystem error on
                                // one kerbal doesn't abort the whole batch —
                                // log Error and continue. Mirrors the per-
                                // entry try/catch isolation in the router
                                // migration helpers.
                                LunaLog.Error(
                                    $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} " +
                                    $"kerbal '{name}' migration failed: {ex.GetType().Name} {ex.Message}. " +
                                    "Continuing with rest of batch. Operator may need to hand-recover " +
                                    "this kerbal's file.");
                                droppedKerbalNames.Add(name);
                            }
                        }
                    }
                }

                // Step 5: Persist BOTH agencies under the dual lock.
                //
                // [Round-1 integration-logic SHOULD FIX scenario 6 — CRASH-WINDOW]
                // Save DESTINATION FIRST (additive), then source (subtractive).
                // A crash between the two leaves the entry in BOTH agencies —
                // recoverable on restart by an operator re-running the command
                // (the same-stamp short-circuit fires for the destination's
                // side; the source-side migration removes the stale duplicate).
                // The opposite order (source first) would leave the entry in
                // NEITHER agency on a mid-pair crash — permanently lost.
                AgencySystem.SaveAgency(destAgencyId);
                if (source != null)
                    AgencySystem.SaveAgency(source.AgencyId);
            });

            if (!mutated)
            {
                // Concurrent-operator short-circuit fired at step 2b. Log at
                // Debug so monitoring scripts don't see noise from idempotent
                // re-runs; the step-1 path's Normal-level log already carries
                // the operator-visible no-op signal.
                LunaLog.Debug(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} no-op result=raced " +
                    $"(another command landed first; stamp already {destAgencyId:N})");
                return true;
            }

            // [Round-1 general-lens MUST FIX #2 — STEP 6/7 REORDER]
            // Step 7 BEFORE step 6: release stale locks FIRST, then RunBackup.
            // Lock release does not require disk flush; if a periodic backup
            // is mid-flight on the backup task thread, gating release-locks
            // behind RunBackup would extend the "source owner sees frozen
            // vessel" UX window by the duration of one backup pass. Lock
            // release closes the post-mutation soak-Finding-2 message-drop
            // window the moment the dual lock unlocks.
            //
            // [Round-1 integration-logic SHOULD FIX scenario 10 — STALE-RENAME]
            // ReleaseStaleVesselLocks now uniformly filters by holder-agency
            // mapping (not by source.OwningPlayerName) so a vessel that
            // accumulated locks before a prior /transferagency rename gets
            // those stale-handle locks released too. The Unassigned-source
            // branch's "ANY non-dest-agency holder" logic was always more
            // correct; we generalise it.
            var releasedCount = ReleaseStaleVesselLocks(movedVesselId, destAgencyId);

            // Step 6: BackupSystem.RunBackup AFTER lock release. Flushes the
            // mutated vessel.OwningAgencyId to disk. Wrapped in try/catch
            // mirroring DeleteAgencyCommand.cs:178-191 — a failed backup leaves
            // the in-memory state correct but disk vessels potentially stale;
            // log loudly and let the operator decide whether to /backup manually.
            try
            {
                BackupSystem.RunBackup();
            }
            catch (Exception e)
            {
                LunaLog.Error(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} BackupSystem.RunBackup failed after " +
                    $"reassign; in-memory state correct but disk vessel may still carry the old agency stamp " +
                    $"until next periodic flush. Manual /backup recommended. Exception: {e.Message}");
            }

            // Step 8: Wire emit ORDER — Visibility first, then source-removal
            // echoes, then destination-add echoes. Same channel (22), ReliableOrdered.
            //
            // (a) Visibility broadcast — tells every client (including source
            //     owner) that V belongs to destAgencyId now. Source owner's
            //     5.18b mirror updates BEFORE the removal echoes prune their
            //     per-router cache, so they don't briefly render "I own V but
            //     V's entries are gone."
            AgencySystemSender.BroadcastVisibilityChange(new List<VesselOwnershipChange>
            {
                new VesselOwnershipChange { VesselId = movedVesselId, NewOwningAgencyId = destAgencyId }
            });

            // (b) Source-owner removal echoes (kolony + orbital). Skipped when
            // source is null or the helper returned an empty result (vessel
            // had no per-router entries in source's partition). Resolve owner
            // client by name; offline source owners get nothing here and pick
            // up the missing entries via the standard handshake catchup path
            // on next connect (catchup ships state.KolonyEntries / OrbitalTransfers
            // post-migration, so the moved entries naturally absent).
            var sourceOwnerClient = string.IsNullOrEmpty(sourceOwnerName)
                ? null
                : ClientRetriever.GetClientByName(sourceOwnerName);
            if (sourceOwnerClient != null && source != null)
            {
                if (kolonyResult != null && kolonyResult.RemovedKeys.Count > 0)
                {
                    AgencySystemSender.SendKolonyStateToOwner(
                        sourceOwnerClient, source.AgencyId, entries: null, removedKeys: kolonyResult.RemovedKeys);
                }
                if (orbitalResult != null && orbitalResult.RemovedTransferGuids.Count > 0)
                {
                    AgencySystemSender.SendOrbitalStateToOwner(
                        sourceOwnerClient, source.AgencyId, entries: null, removedTransferGuids: orbitalResult.RemovedTransferGuids);
                }
            }

            // (c) Destination-owner add echoes (kolony + orbital). Resolve dest
            // owner client by name; offline dest owners get nothing here and
            // pick up the added entries via their next handshake catchup.
            var destOwnerClient = string.IsNullOrEmpty(destOwnerName)
                ? null
                : ClientRetriever.GetClientByName(destOwnerName);
            if (destOwnerClient != null)
            {
                if (kolonyResult != null && kolonyResult.AddedEntries.Count > 0)
                {
                    AgencySystemSender.SendKolonyStateToOwner(destOwnerClient, destAgencyId, kolonyResult.AddedEntries);
                }
                if (orbitalResult != null && orbitalResult.AddedEntries.Count > 0)
                {
                    AgencySystemSender.SendOrbitalStateToOwner(destOwnerClient, destAgencyId, orbitalResult.AddedEntries);
                }
            }

            // [Stage 6 / Phase 6.8] Per-kerbal wire push to source + dest
            // owners. Each moved kerbal needs to flow to dest owner's client
            // as a KerbalProtoMsgData (so their KSP picks up the new roster
            // entry) and to source owner's client as a KerbalRemoveMsgData
            // (so their KSP drops the no-longer-owned roster entry). Offline
            // owners get the right state via their next handshake
            // KerbalsRequest catch-up — no push, no harm.
            //
            // Wire push runs OUTSIDE the dual-lock critical section to keep
            // the lock window short. Same shape as the per-router echo emit
            // above (steps 8b + 8c at lines 467-497) which is also outside
            // the lock for the same reason. The per-kerbal byte snapshots
            // captured under the lock in movedKerbalBytes are stable —
            // the source files are already deleted and the dest files won't
            // be mutated by any non-locked path (HandleKerbalProto for
            // destination's owner would acquire the same agency lock we
            // just released).
            if (migrateKerbals && movedKerbalBytes.Count > 0)
            {
                foreach (var entry in movedKerbalBytes)
                {
                    if (destOwnerClient != null)
                    {
                        var protoMsg = ServerContext.ServerMessageFactory
                            .CreateNewMessageData<KerbalProtoMsgData>();
                        protoMsg.Kerbal = new KerbalInfo
                        {
                            KerbalName = entry.Key,
                            KerbalData = entry.Value,
                            NumBytes = entry.Value.Length,
                        };
                        MessageQueuer.SendToClient<KerbalSrvMsg>(destOwnerClient, protoMsg);
                    }

                    if (sourceOwnerClient != null)
                    {
                        var removeMsg = ServerContext.ServerMessageFactory
                            .CreateNewMessageData<KerbalRemoveMsgData>();
                        removeMsg.KerbalName = entry.Key;
                        MessageQueuer.SendToClient<KerbalSrvMsg>(sourceOwnerClient, removeMsg);
                    }
                }
            }

            // [Round-1 integration-logic MUST FIX scenario 7 — THIRD-AGENCY CROSS-REF]
            // Orbital transfers form a graph: agency C may hold a transfer
            // where OriginVesselId or DestinationVesselId points at our
            // movedVessel. The Migrate*ForVesselTransfer helpers ONLY scan
            // source. C's stranded reference stays untouched in C — Bob
            // will receive a delivery whose authoritative side is C, and
            // C's projector will continue to render an in-flight transfer
            // whose Destination now belongs to Bob's agency. The 5.17a
            // cross-agency guard on Deliver-prefix consumers will reject
            // the actual handover. Operator needs to know.
            var thirdAgencyOrphans = InspectThirdAgencyCrossReferences(
                movedVesselId, sourceAgencyId, destAgencyId);

            // Operator-visible summary log. Grammar mirrors /deleteagency +
            // /transferagency for the GUI launcher's (verb, id, key=value, count)
            // regex. The result=transferred key (Round-1 consumer-lens MUST FIX)
            // lets a GUI parser distinguish "real mutation happened" from the
            // same-stamp short-circuit log line which carries result=noop.
            //
            // GUI launcher post-command refresh: stdout summary line is the
            // signal for "command landed"; the GUI should then re-invoke
            // /listagencies or poll the /fork web endpoint for fresh per-agency
            // state. AgencyVisibilityMsgData + per-router echoes flow to
            // connected clients via Lidgren; the GUI itself is not a peer.
            var sourceLabel = sourceIsUnassigned
                ? "Unassigned"
                : (source != null ? $"{source.AgencyId:N}" : $"orphan:{sourceAgencyId:N}");
            var kolonyMoved = kolonyResult?.RemovedKeys.Count ?? 0;
            var orbitalMoved = orbitalResult?.RemovedTransferGuids.Count ?? 0;
            var orbitalKept = orbitalResult?.OriginOnlyKeptGuids.Count ?? 0;
            var planetaryAffected = planetaryResult?.AffectedKeys.Count ?? 0;
            // [Mod-compat S2] Scan migration is single-entry (vessel-keyed; at
            // most one record moves per call). Non-Empty RemovedVesselId =
            // entry moved; Empty = source had no scanner record (or the
            // RemovedVesselId field defaults to Empty when the helper short-
            // circuited).
            var scanMoved = (scanResult != null && scanResult.RemovedVesselId != Guid.Empty) ? 1 : 0;
            // [Phase 6.8] Kerbal-migration audit counts. kerbals-moved counts
            // successful file moves; kerbals-dropped counts per-kerbal DROPs
            // (collision under lock, source-file-missing, per-kerbal IO failure).
            // Under gate=off both are zero by construction (the kerbal step
            // didn't run).
            var kerbalsMoved = movedKerbalBytes.Count;
            var kerbalsDropped = droppedKerbalNames.Count;
            LunaLog.Normal(
                $"[fix:per-agency-career] setvesselagency {movedVesselId:N} result=transferred source={sourceLabel} " +
                $"dest={destAgencyId:N} dest-owner='{destOwnerName}' kolony-moved={kolonyMoved} " +
                $"orbital-moved={orbitalMoved} orbital-origin-kept={orbitalKept} " +
                $"planetary-retained-in-source={planetaryAffected} scan-moved={scanMoved} " +
                $"kerbals-moved={kerbalsMoved} kerbals-dropped={kerbalsDropped} " +
                $"kerbals-legacy-stranded={kerbalsLegacyStranded} " +
                $"third-agency-stranded={thirdAgencyOrphans} released-locks={releasedCount}");

            // Per-kerbal audit lines for the moved set + dropped set. Mirrors
            // the per-key orbital + planetary audit lines below — operators
            // grepping for a specific kerbal name get one line per migration
            // event. [fix:per-agency-kerbal-roster] prefix matches Phase 6.4
            // / 6.5 / 6.7 grammar so GUI launchers can filter the whole
            // kerbal-roster surface under one regex. Each line carries
            // result={moved|dropped} for parity with Phase 6.7's grammar
            // (consumer-lens v1 SHOULD FIX S2 — GUIs filtering by `result=`
            // catch per-kerbal trail consistently with the summary line).
            foreach (var movedName in movedKerbalBytes.Keys)
            {
                LunaLog.Normal(
                    $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} result=moved kerbal='{movedName}' " +
                    $"source={sourceLabel} dest={destAgencyId:N}");
            }
            foreach (var droppedName in droppedKerbalNames)
            {
                LunaLog.Normal(
                    $"[fix:per-agency-kerbal-roster] setvesselagency {movedVesselId:N} result=dropped kerbal='{droppedName}' " +
                    $"source={sourceLabel} dest={destAgencyId:N} (see Error/Warning above for reason)");
            }

            // Per-guid audit lines for the orbital Origin-only KEEP set
            // (pre-spec §4.e operator info log — Q1 KEEP retains the launch
            // obligation in source agency; operator wants the per-guid
            // detail for grep). Kept on [fix:MKS-R2] since these are
            // MKS-specific migration-audit lines, not operator-command-grain
            // lines a GUI launcher's per-agency-career filter would route.
            if (orbitalResult != null)
            {
                foreach (var keptGuid in orbitalResult.OriginOnlyKeptGuids)
                {
                    LunaLog.Normal(
                        $"[fix:MKS-R2] setvesselagency {movedVesselId:N} kept origin-transfer={keptGuid:N} in source agency");
                }
            }

            // Per-key audit lines for the planetary affected-keys set (Q2
            // NO-MIGRATE — entries stay in source as historical body-pool
            // contributions). One line per body+resource key.
            if (planetaryResult != null)
            {
                foreach (var key in planetaryResult.AffectedKeys)
                {
                    // Key shape: $"{bodyIndex}|{resourceName}".
                    LunaLog.Normal(
                        $"[fix:MKS-R2] setvesselagency {movedVesselId:N} planetary-retained-in-source key={key}");
                }
            }

            return true;
        }

        /// <summary>
        /// Runs <paramref name="action"/> under the correct lock-acquire order
        /// per pre-spec §4.e dual-lock contract. Source and destination agency
        /// locks are acquired in <see cref="Guid.CompareTo"/> order
        /// (lower-comparing AgencyId first). When <paramref name="source"/> is
        /// null (Unassigned-vessel reassignment), only the destination lock is
        /// acquired — there's no source AgencyState to read.
        /// </summary>
        private static void RunUnderLockOrder(AgencyState source, AgencyState destination, Action action)
        {
            if (source == null)
            {
                lock (AgencySystem.GetAgencyLock(destination.AgencyId))
                {
                    action();
                }
                return;
            }

            // Guid.CompareTo (byte-form). String-form ordering would disagree
            // with byte-form ordering for some Guids; .NET Guid.CompareTo is
            // the authoritative comparison.
            if (source.AgencyId.CompareTo(destination.AgencyId) < 0)
            {
                lock (AgencySystem.GetAgencyLock(source.AgencyId))
                {
                    lock (AgencySystem.GetAgencyLock(destination.AgencyId))
                    {
                        action();
                    }
                }
            }
            else
            {
                lock (AgencySystem.GetAgencyLock(destination.AgencyId))
                {
                    lock (AgencySystem.GetAgencyLock(source.AgencyId))
                    {
                        action();
                    }
                }
            }
        }

        /// <summary>
        /// Step 7 of the 9-step contract. Releases vessel-scoped locks
        /// (Control / Update / UnloadedUpdate) on the moved vessel that would
        /// otherwise produce a "frozen vessel" UX for the prior holder under
        /// the post-mutation 5.17a + soak-Finding-2 write-path guards.
        ///
        /// <para><b>Unified filter (Round-1 integration-logic SHOULD FIX
        /// scenario 10):</b> walks every authenticated player's locks and
        /// releases vessel-scoped locks on the moved vessel held by any
        /// player whose <see cref="AgencySystem.AgencyByPlayerName"/>
        /// mapping does NOT resolve to <paramref name="destAgencyId"/>. This
        /// is structurally more correct than a "filter by source.OwningPlayerName"
        /// branch because:
        /// <list type="bullet">
        ///   <item><b>Typical A→B:</b> only source's player has the lock; the
        ///         mapping check matches them and releases.</item>
        ///   <item><b>Spec §10 Q3 Unassigned bypass:</b> any agency may have
        ///         held a lock on a sentinel vessel; all non-dest holders get
        ///         released.</item>
        ///   <item><b>Orphan source:</b> the prior holder either has been
        ///         healed via <c>AgencySystem.cs:1335</c> boot path (mapping
        ///         removed) or still holds the stale orphan mapping; both
        ///         fall through the dest-mapping skip and get released.</item>
        ///   <item><b>Stale-rename pre-acquire (the scenario-10 hazard):</b>
        ///         Alice acquired a lock under her old player handle BEFORE
        ///         a <c>/transferagency</c> rename moved her agency to
        ///         Charlie. The <see cref="LockDefinition.PlayerName"/> on
        ///         the lock is still "alice". A filter-by-source.OwningPlayerName
        ///         using "charlie" would miss it. This unified walk reads
        ///         each lock's actual PlayerName and routes through
        ///         AgencyByPlayerName — Alice's stale handle maps to the
        ///         (now-different) agency that owned her at the rename, but
        ///         that mapping persists post-rename as "alice→aliceAgency"
        ///         (transferagency renames OwningPlayerName but does NOT
        ///         remove the prior name from AgencyByPlayerName until the
        ///         prior owner reconnects fresh). The lock release fires
        ///         correctly. Net effect: stale-rename pre-acquire locks
        ///         are released along with the rest.</item>
        /// </list></para>
        ///
        /// <para><b>Complexity</b> O(authenticated_clients × locks_per_client).
        /// For a 50-player server with ~100 locks each = 5000 iterations per
        /// command. Acceptable for an admin operation that fires at operator
        /// cadence.</para>
        /// </summary>
        private static int ReleaseStaleVesselLocks(Guid movedVesselId, Guid destAgencyId)
        {
            // Snapshot locks to release first — the LockStore mutates during
            // ReleaseAndSendLockReleaseMessage and we don't enumerate while
            // modifying.
            var toRelease = new List<LockDefinition>();
            var clientCache = new Dictionary<string, ClientStructure>(StringComparer.Ordinal);

            foreach (var client in ClientRetriever.GetAuthenticatedClients())
            {
                if (client == null || string.IsNullOrEmpty(client.PlayerName)) continue;

                // Skip the destination agency's player(s) — they're the new
                // owner and shouldn't be interrupted. The mapping check tolerates
                // missing entries (returns false → not destAgency → release).
                if (AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var holderAgency)
                    && holderAgency == destAgencyId)
                {
                    continue;
                }

                var locksHeld = LockSystem.LockQuery.GetAllPlayerLocks(client.PlayerName);
                foreach (var lockDef in locksHeld)
                {
                    if (!VesselScopedLockTypes.Contains(lockDef.Type)) continue;
                    if (lockDef.VesselId != movedVesselId) continue;
                    toRelease.Add(lockDef);
                    clientCache[client.PlayerName] = client;
                }
            }

            if (toRelease.Count == 0) return 0;

            foreach (var lockDef in toRelease)
            {
                // Round-1 consumer-lens SHOULD FIX — added vessel={...:N} key
                // so a (verb, vessel-id) regex across per-agency admin command
                // log lines matches this released-lock audit consistently with
                // /deleteagency + /transferagency.
                LunaLog.Normal(
                    $"[fix:per-agency-career] setvesselagency {movedVesselId:N} released-lock " +
                    $"vessel={lockDef.VesselId:N} player='{lockDef.PlayerName}' type={lockDef.Type}");

                clientCache.TryGetValue(lockDef.PlayerName, out var holderClient);
                LockSystemSender.ReleaseAndSendLockReleaseMessage(holderClient, lockDef);
            }
            return toRelease.Count;
        }

        /// <summary>
        /// [Round-1 integration-logic MUST FIX scenario 7 — THIRD-AGENCY
        /// CROSS-REFERENCE INSPECTION] Scans every agency OTHER than source
        /// + destination for orbital transfers whose
        /// <see cref="AgencyOrbitalTransferEntry.DestinationVesselId"/> or
        /// <see cref="AgencyOrbitalTransferEntry.OriginVesselId"/> points at
        /// the moved vessel. Emits one Warning per stranded reference so
        /// the operator can investigate (and ideally /kick the third agency's
        /// player + manually re-route the transfer via Universe-edit; no
        /// admin command exists today to do this in-band).
        ///
        /// <para>Returns the count of stranded entries for the operator
        /// summary line. Zero is the typical case; non-zero is the soak-
        /// pattern that pre-spec §4.e didn't anticipate — a graph topology
        /// where transfers from C to V get orphaned when V transfers to
        /// Bob, leaving C's projector continuing to render an in-flight
        /// transfer the destination side will cross-agency-reject on
        /// Deliver.</para>
        ///
        /// <para>Kolony + planetary partitions are NOT scanned because
        /// neither carries a cross-vessel reference: kolony entries are
        /// vessel-keyed (the moved vessel's entries are already migrated by
        /// the kolony helper); planetary entries are body-keyed and the
        /// OwningVesselId field is incidental record-keeping (the entries
        /// are NOT migrated by the planetary helper per Q2 NO-MIGRATE).
        /// Only orbital is the graph-shaped partition.</para>
        /// </summary>
        private static int InspectThirdAgencyCrossReferences(
            Guid movedVesselId, Guid sourceAgencyId, Guid destAgencyId)
        {
            var stranded = 0;
            foreach (var kvp in AgencySystem.Agencies)
            {
                var agencyId = kvp.Key;
                var agencyState = kvp.Value;
                if (agencyId == sourceAgencyId || agencyId == destAgencyId) continue;
                if (agencyState == null) continue;

                // Read under the agency lock — concurrent router upsert
                // mutates OrbitalTransfers entries; iteration without the
                // lock could trip InvalidOperationException.
                lock (AgencySystem.GetAgencyLock(agencyId))
                {
                    foreach (var t in agencyState.OrbitalTransfers)
                    {
                        var entry = t.Value;
                        if (entry == null) continue;
                        var matchesDest = entry.DestinationVesselId == movedVesselId;
                        var matchesOrigin = entry.OriginVesselId == movedVesselId;
                        if (!matchesDest && !matchesOrigin) continue;

                        var refType = matchesDest && matchesOrigin
                            ? "both-Origin-and-Destination"
                            : (matchesDest ? "Destination" : "Origin");
                        LunaLog.Warning(
                            $"[fix:MKS-R2] setvesselagency {movedVesselId:N} third-agency-stranded-transfer " +
                            $"agency={agencyId:N} transfer={entry.TransferGuid:N} role={refType}. " +
                            "Operator must manually reroute the transfer or /kick the third agency's owner " +
                            "(no admin command exists today to in-band edit a third agency's orbital partition).");
                        stranded++;
                    }
                }
            }
            return stranded;
        }

        /// <summary>
        /// [Stage 6 / Phase 6.8] Extracts the list of kerbal names aboard
        /// <paramref name="vesselId"/> by scanning its serialised ConfigNode
        /// text for <c>crew = NAME</c> lines. Returns true on success (even
        /// when the result list is empty — unmanned vessels are the common
        /// case AND vessel-not-in-store is the secondary common case where
        /// <c>GetVesselInConfigNodeFormat</c> returns null). Returns false
        /// ONLY when the vessel's <c>ToString</c> threw an exception — the
        /// null-or-empty-text branch returns true with an empty list because
        /// "no crew was found" is the correct semantic answer (consumer-lens
        /// v1 SHOULD FIX S3 — earlier XML claimed false for the null branch
        /// but the implementation diverged; XML now matches code). Pure
        /// text scan — same shape as the K1 grief guard at
        /// <see cref="KerbalSystem.CanRemoveKerbalUnderK1"/>, but inverted
        /// (collect names, not contains-check one).
        ///
        /// <para><b>Needle:</b> exact <c>"crew = "</c> match. KSP's vessel
        /// format uses this verbatim at <c>protoModuleCrew</c> top-level —
        /// see existing usage at <see cref="KerbalSystem.CanRemoveKerbalUnderK1"/>
        /// which has been stable since Stage 5.17e-8 (2026-05-17). The needle
        /// is depth-unaware; if a soak ever surfaces false positives (a
        /// nested ConfigNode child literally named <c>crew</c>), switch to
        /// the depth-aware text walker at
        /// <see cref="Server.System.Agency.AgencyWolfMigration.TryRewriteKerbalText"/>
        /// which tracks brace depth for top-level field detection.</para>
        ///
        /// <para><b>Dedup:</b> a kerbal aboard multiple parts of the same
        /// vessel produces multiple <c>crew = NAME</c> lines. The result
        /// list is deduplicated via <see cref="HashSet{T}"/>.</para>
        /// </summary>
        internal static bool ExtractCrewFromVessel(Guid vesselId, out List<string> crewNames)
        {
            crewNames = new List<string>();
            string vesselText;
            try
            {
                vesselText = VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId);
            }
            catch (Exception)
            {
                return false;
            }
            if (string.IsNullOrEmpty(vesselText))
                return true; // valid empty-result branch (rare)

            // Line-by-line scan keeps the algorithm pure (no regex backtrack
            // surprises on the full vessel text, which can be 100kB+). Trim
            // leading whitespace because KSP indents `crew = NAME` lines
            // inside the PART block.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = new StringReader(vesselText))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("crew = ", StringComparison.Ordinal))
                        continue;
                    var name = trimmed.Substring("crew = ".Length).Trim();
                    if (string.IsNullOrEmpty(name)) continue;

                    // [Stage 6 Phase 6.9-hardening — security-lens MUST FIX #1
                    // defense-in-depth] The `crew = NAME` line came from the
                    // vessel proto, which is written by a (potentially modified)
                    // client via VesselProtoMsgData. The v4 cross-agency write
                    // guard at HandleVesselProto prevents an attacker from
                    // writing protos for OTHER agencies' vessels, but the
                    // attacker's OWN vessel proto could contain forged
                    // `crew = ../../../etc/passwd` lines that would feed the
                    // unsafe name straight through to the migration loop's
                    // path-construction sinks (Path.Combine in
                    // ResolveKerbalSourcePathForMove + CheckDestinationCollisions
                    // + the per-kerbal move). Validate here so a malicious
                    // vessel proto on the operator's reassign command fails
                    // closed at the name-extraction boundary.
                    if (!KerbalNameValidator.IsValid(name, out var reason))
                    {
                        // Snippet-truncated log to prevent recursive
                        // amplification on a malformed long name. Same shape
                        // as KerbalMsgReader.RejectIfInvalidKerbalName.
                        var snippet = name.Length <= 32 ? $"'{name}'" : $"'{name.Substring(0, 32)}...'";
                        LunaLog.Warning(
                            $"[fix:per-agency-kerbal-roster] setvesselagency {vesselId:N} extracted crew-name {snippet} " +
                            $"failed validation ({reason}); skipping. Vessel proto may have been tampered with — " +
                            "inspect Universe/Vessels/{vid}.txt for unexpected `crew = ...` lines.");
                        continue;
                    }

                    if (seen.Add(name))
                        crewNames.Add(name);
                }
            }
            return true;
        }

        /// <summary>
        /// [Stage 6 / Phase 6.8] Lock-free pre-check: for each name in
        /// <paramref name="names"/>, does
        /// <c>Universe/Agencies/{destAgencyId:N}/Kerbals/{name}.txt</c>
        /// already exist? Returns the colliding names (in input order;
        /// unordered set semantics — duplicates are not introduced because
        /// <see cref="ExtractCrewFromVessel"/> already deduplicated the
        /// caller's name list).
        ///
        /// <para><b>Safety semantics</b> (consumer-lens v1 CONSIDER C2):
        /// empty return list means "no collision was pre-detected at scan
        /// time" — it does NOT mean "no collision will be found at migration
        /// time." A destination owner's
        /// <see cref="KerbalSystem.TryWriteKerbalProtoPerAgency"/> landing
        /// between this pre-check and the dual-lock acquire could create a
        /// same-name file. The under-lock optimistic re-check inside step
        /// 4b of <see cref="Execute"/> is the authoritative final check
        /// (lock-held against any concurrent
        /// <c>TryWriteKerbalProtoPerAgency</c> for the destination agency).
        /// Callers MUST treat this pre-check as a fast-fail optimisation
        /// only, never as a sufficient correctness gate.</para>
        ///
        /// <para><b>Why lock-free</b>: holding the destination agency lock
        /// during this scan would block all concurrent destination-owner
        /// <c>HandleKerbalProto</c> writes for the duration of the
        /// pre-flight window (which is operator-paced — could be seconds
        /// on a slow IOps disk + many crew). The lock-free + under-lock
        /// re-check pair generalises Phase 6.5's
        /// <see cref="AgencySystem.AgencyByPlayerName"/> lock-free resolve
        /// + <see cref="AgencySystem.Agencies"/> <c>ContainsKey</c>
        /// under-lock re-check from in-memory dictionary state to disk-file
        /// state. See CLAUDE.md Stack Notes "Stage 6 Phase 6.8 — two-phase
        /// collision check" entry for the general pattern.</para>
        /// </summary>
        private static List<string> CheckDestinationCollisions(List<string> names, Guid destAgencyId)
        {
            var collisions = new List<string>();
            var destKerbalsDir = AgencySystem.GetKerbalsPathForAgency(destAgencyId);
            foreach (var name in names)
            {
                var destPath = Path.Combine(destKerbalsDir, name + ".txt");
                if (FileHandler.FileExists(destPath))
                    collisions.Add(name);
            }
            return collisions;
        }

        /// <summary>
        /// [Stage 6 / Phase 6.8] Resolves the per-kerbal source-file path for
        /// the migration step. Mirrors
        /// <see cref="Server.System.Agency.AgencyWolfMigration.ResolveKerbalSourcePath"/>
        /// + its upgrade-fallback probe at <c>AgencyWolfMigration.cs:434-451</c>.
        /// The migration target is ALWAYS per-agency (gate=on is the entry
        /// condition for this whole code path — gate=off skips the kerbal
        /// step entirely), but the SOURCE can come from legacy under three
        /// distinct upgrade scenarios documented below.
        ///
        /// <para><b>Three-tier probe (Phase 6.8 upgrade-lens v1 MUST FIX Finding 1):</b>
        /// <list type="number">
        ///   <item><b>Tier 1 — per-agency canonical path</b>: returns
        ///         <c>Universe/Agencies/{sourceAgencyId:N}/Kerbals/{name}.txt</c>
        ///         when it exists. The healthy steady-state path for any
        ///         Phase-6.5+-shipped universe.</item>
        ///   <item><b>Tier 2 — legacy upgrade-fallback</b>: probes
        ///         <c>Universe/Kerbals/{name}.txt</c> when Tier 1 misses,
        ///         logs a Warning naming both paths so the upgrade hazard is
        ///         audible. Three distinct scenarios feed Tier 2:
        ///         <list type="bullet">
        ///           <item><b>(a) Normal registered source + legacy-stranded
        ///                 kerbal:</b> operator opted into
        ///                 <c>AllowEnablePerAgencyKerbalsOnExistingUniverse=true</c>
        ///                 on a populated universe; Alice's agency was minted
        ///                 with stock-4 templates but her recruited kerbals
        ///                 live in legacy. Symmetric with Phase 6.7's
        ///                 cascade-fallback.</item>
        ///           <item><b>(b) Unassigned-sentinel source (Guid.Empty):</b>
        ///                 vessel stamp is Empty (pre-0.31 sentinel per spec
        ///                 §10 Q3); kerbals never had a per-agency owner so
        ///                 only legacy has them.</item>
        ///           <item><b>(c) Orphan source:</b> non-Empty stamp but
        ///                 <c>AgencyState</c> file lost; per-agency Kerbals
        ///                 subdir may also be gone. Legacy probe is best-
        ///                 effort recovery.</item>
        ///         </list>
        ///         The Warning text names the originating scenario via the
        ///         <paramref name="upgradeBranchLabel"/> caller-supplied
        ///         label (Phase 6.8 upgrade-lens v1 CONSIDER F6).</item>
        ///   <item><b>Tier 3 — both paths absent:</b> returns the per-agency
        ///         path so the caller's <c>FileExists</c> check DROPs with a
        ///         "no file anywhere" Warning. Empty-string-return was
        ///         considered but adds caller-side null-handling noise — a
        ///         path-that-fails-FileExists is cleaner.</item>
        /// </list></para>
        ///
        /// <para><b>Why all source branches probe legacy, not just
        /// Unassigned/orphan</b> (Phase 6.8 upgrade-lens v1 MUST FIX
        /// Finding 1): pre-fix Phase 6.8 restricted the legacy probe to
        /// <c>sourceIsUnassignedOrOrphan=true</c>, which silently abandoned
        /// the dominant upgrade case (operator with
        /// <c>AllowEnablePerAgencyKerbalsOnExistingUniverse=true</c> on a
        /// populated universe — registered-agency source with legacy-stranded
        /// kerbals). Phase 6.7's cascade-fallback already covered this for
        /// <c>/deleteagency</c> at <c>AgencyWolfMigration.cs:434-451</c>;
        /// Phase 6.8 now mirrors the same probe for <c>/setvesselagency</c>
        /// so the symmetry holds across both per-agency admin commands.</para>
        /// </summary>
        private static string ResolveKerbalSourcePathForMove(string kerbalName, Guid sourceAgencyId, bool sourceIsUnassignedOrOrphan, out bool resolvedViaLegacy)
        {
            resolvedViaLegacy = false;

            var perAgencyPath = sourceAgencyId == Guid.Empty
                ? null
                : Path.Combine(AgencySystem.GetKerbalsPathForAgency(sourceAgencyId), kerbalName + ".txt");

            // Tier 1: per-agency canonical path. Probe always (regardless of
            // source-status flag) — orphan stamps could legitimately still
            // have a per-agency Kerbals subdir from the lost agency's
            // lifetime, and Unassigned-sentinel (Empty) skips this tier
            // because perAgencyPath is null.
            if (!string.IsNullOrEmpty(perAgencyPath) && FileHandler.FileExists(perAgencyPath))
                return perAgencyPath;

            // Tier 2: legacy upgrade-fallback. Fires for every source branch
            // when the per-agency path misses. Branch-aware Warning so
            // operators see WHICH upgrade scenario triggered the probe.
            var legacyPath = Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt");
            if (FileHandler.FileExists(legacyPath))
            {
                string branchLabel;
                if (sourceAgencyId == Guid.Empty)
                    branchLabel = "Unassigned-sentinel source (Guid.Empty; spec §10 Q3 pre-0.31 vessel) — legacy is the only place this kerbal could live";
                else if (sourceIsUnassignedOrOrphan)
                    branchLabel = $"orphan source (AgencyId {sourceAgencyId:N} has no AgencyState in Agencies registry) — per-agency subdir may have been lost with the agency file";
                else
                    branchLabel = $"registered source {sourceAgencyId:N} but kerbal stranded in legacy — AllowEnablePerAgencyKerbalsOnExistingUniverse=true on a populated v0-v7 universe is the typical cause";

                LunaLog.Warning(
                    $"[fix:per-agency-kerbal-roster] setvesselagency kerbal '{kerbalName}' resolved via legacy " +
                    $"fallback at {legacyPath} (per-agency path {perAgencyPath ?? "<source-is-Empty>"} absent). " +
                    $"Upgrade scenario: {branchLabel}. Migration proceeds; legacy file deleted post-move.");
                resolvedViaLegacy = true;
                return legacyPath;
            }

            // Tier 3: neither path has the file. Return per-agency path so
            // caller's FileExists check produces the "no file anywhere"
            // diagnostic.
            return perAgencyPath ?? legacyPath;
        }
    }
}
