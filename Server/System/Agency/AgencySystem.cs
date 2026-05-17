using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Server.System.Agency
{
    /// <summary>
    /// Server-side registry + persistence lifecycle for per-agency career state. Holds the
    /// canonical <see cref="AgencyState"/> for every player who has connected to this server.
    /// Hooked into <see cref="HandshakeSystem"/> on player auth (register-or-load) and into
    /// <see cref="MainServer"/> boot (load every persisted agency).
    ///
    /// **Gated on <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>.** Every public
    /// entry point early-returns when the setting is <c>false</c>. The dual-mode guarantee
    /// (Stage 5 acceptance criteria, spec §11) requires that with the setting off, this
    /// system has zero observable effect — no disk reads, no disk writes, no registry
    /// entries. The shared-agency code path (<see cref="Share*System"/> family) is the
    /// authority in that mode and AgencySystem is invisible.
    ///
    /// Stage 5.15a scope is lifecycle only — register, load, save, boot-load. Wire-protocol
    /// broadcasting of agency state to clients lands in 5.15b/5.15c. Per-agency routing of
    /// the <c>Share*</c> mutations lands in 5.17b. Until then, the registry is populated but
    /// nothing consumes it on the wire.
    /// </summary>
    public static class AgencySystem
    {
        /// <summary>
        /// Authoritative registry. Key is the canonical <see cref="AgencyState.AgencyId"/> Guid
        /// per Q7 sign-off — survives player renames and is the on-disk filename.
        /// </summary>
        public static ConcurrentDictionary<Guid, AgencyState> Agencies { get; } =
            new ConcurrentDictionary<Guid, AgencyState>();

        /// <summary>
        /// Convenience index for the <c>player → agency</c> lookup. Updated in lockstep with
        /// <see cref="Agencies"/>. Player renames are admin-driven (Stage 5.18d
        /// <c>transferagency</c> command); until then the OwningPlayerName field on
        /// AgencyState is treated as stable.
        ///
        /// **Mutation ordering rule (Stage 5.18d caller contract).** Any future writer that
        /// reassigns a player → agency mapping (transferagency, admin overrides) MUST:
        ///   1. acquire <see cref="PlayerNameLocks"/> for the moving player name,
        ///   2. update <see cref="Agencies"/> entries (per-agency lock for each side),
        ///   3. persist via <see cref="SaveAgency"/>,
        ///   4. ONLY THEN flip the <see cref="AgencyByPlayerName"/> entry.
        /// The per-vessel proto path in <see cref="Server.Message.VesselMsgReader.HandleVesselProto"/>
        /// snapshots this index on the receive thread — a torn read where the index points
        /// at an agency whose state hasn't been persisted yet would stamp a vessel with an
        /// id that fails the next disk round-trip.
        /// </summary>
        public static ConcurrentDictionary<string, Guid> AgencyByPlayerName { get; } =
            new ConcurrentDictionary<string, Guid>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency lock anchor. Mutations to a single AgencyState (RegisterAgency creating
        /// + initial save, SaveAgency overwriting) acquire this object so Serialize doesn't
        /// race a concurrent field write. Follows the BUG-033 precedent from
        /// <see cref="Server.System.Scenario.ScenarioDataUpdater.GetSemaphore"/> — per-key
        /// locks via <see cref="ConcurrentDictionary.GetOrAdd"/>.
        ///
        /// Exposed as <see cref="GetAgencyLock"/> (internal) so future Stage 5.17b
        /// <c>Share*</c> writers that mutate <see cref="AgencyState"/> fields can hold the
        /// same lock around their mutation. Without this, a concurrent <see cref="SaveAgency"/>
        /// would <see cref="AgencyState.Serialize"/> a non-atomic multi-field snapshot.
        /// </summary>
        private static readonly ConcurrentDictionary<Guid, object> AgencyLocks =
            new ConcurrentDictionary<Guid, object>();

        /// <summary>
        /// Per-player-name lock anchor for the <see cref="RegisterAgency"/> lookup-or-create
        /// path. Without it, two concurrent <see cref="OnPlayerAuthenticated"/> calls for the
        /// same name (e.g. a fast reconnect racing the in-flight auth of a stale connection,
        /// arriving on different Lidgren receive threads) can both miss the registry, both
        /// mint distinct <c>Guid.NewGuid()</c> AgencyIds, both <see cref="ConcurrentDictionary.TryAdd"/>
        /// succeed, and leave one agency orphaned on disk + a non-deterministic last-writer-wins
        /// on the player-name index. The lock serializes per name so the second call sees the
        /// first's commit and returns the existing agency.
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> PlayerNameLocks =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        /// <summary>
        /// Boot-time loader. Enumerates <see cref="AgencyState.AgenciesPath"/>, parses every
        /// <c>{guid}.txt</c>, and populates <see cref="Agencies"/> + <see cref="AgencyByPlayerName"/>.
        /// Per-file failures are logged and skipped — one corrupt agency file must not block
        /// the rest of the server from booting (spec §3 isolation principle, same shape as
        /// per-contract isolation in Stage 5.17b).
        ///
        /// No-op when <see cref="GameplaySettingsDefinition.PerAgencyCareer"/> is false.
        /// </summary>
        public static void LoadExistingAgencies()
        {
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
                return;

            var folder = AgencyState.AgenciesPath;
            if (!FileHandler.FolderExists(folder))
                return;

            var loadedCount = 0;
            foreach (var filePath in FileHandler.GetFilesInPath(folder))
            {
                if (Path.GetExtension(filePath) != ".txt")
                    continue;

                try
                {
                    var state = LoadAgencyFromFile(filePath);
                    if (state == null)
                        continue;

                    if (Agencies.TryAdd(state.AgencyId, state))
                    {
                        if (!string.IsNullOrEmpty(state.OwningPlayerName))
                            AgencyByPlayerName[state.OwningPlayerName] = state.AgencyId;
                        loadedCount++;
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[fix:per-agency-career] Failed to load agency file {Path.GetFileName(filePath)}: {e}");
                }
            }

            LunaLog.Normal($"[fix:per-agency-career] Loaded {loadedCount} per-agency career state(s) from disk");

            // [Stage 5.17a round-1 upgrade-lens review] Per-orphan-agency vessel warning.
            // If a vessel's lmpOwningAgency points at an agency-id that didn't load (file
            // corrupted + per-file isolation skip; manual delete from Universe/Agencies/;
            // .bak rotation lost; etc.), the owning player on reconnect will mint a NEW
            // agency under a DIFFERENT id — and Stage 5.17a's cross-agency rejection will
            // lock them out of their own vessels. Loud diagnostic at boot lets the operator
            // recover (restore .bak, transferagency, or accept the loss before it bites).
            //
            // Walk the already-loaded vessel set (LoadExistingVessels ran first at boot per
            // MainServer ordering). Deduplicate per orphan agency id and report the vessel
            // count. Quiet when no orphans found.
            WarnAboutOrphanedVessels();
        }

        /// <summary>
        /// Operator-facing boot diagnostic: scan <see cref="VesselStoreSystem.CurrentVessels"/>
        /// for any vessel stamped with an <c>OwningAgencyId</c> that is not present in the
        /// <see cref="Agencies"/> registry. Each orphan id produces one warning line listing
        /// the affected vessel count. Stage 5.17a hardening: without this, a corrupted agency
        /// file (per-file isolation skip in <see cref="LoadExistingAgencies"/>) silently locks
        /// the affected player out of their own vessels at next reconnect.
        /// </summary>
        private static void WarnAboutOrphanedVessels()
        {
            var orphanCounts = new Dictionary<Guid, int>();
            foreach (var vessel in VesselStoreSystem.CurrentVessels.Values)
            {
                var ownerId = vessel.OwningAgencyId;
                if (ownerId == Guid.Empty)
                    continue; // Unassigned-sentinel per spec §10 Q3 — not orphan.
                if (Agencies.ContainsKey(ownerId))
                    continue;
                orphanCounts.TryGetValue(ownerId, out var count);
                orphanCounts[ownerId] = count + 1;
            }

            foreach (var kvp in orphanCounts)
            {
                LunaLog.Warning($"[fix:per-agency-career] {kvp.Value} vessel(s) reference agency {kvp.Key:N} which did not load. The owning player will mint a new agency on reconnect and be locked out of these vessels by the cross-agency check. Restore Universe/Agencies/{kvp.Key:N}.txt(.bak) or admin transferagency to recover.");
            }
        }

        /// <summary>
        /// Registers a new agency for the given player, or returns the player's existing
        /// agency if one is already in the registry. Idempotent — calling twice for the
        /// same player returns the same <see cref="AgencyState"/> both times. The starting
        /// scalars (Funds, Science, Reputation) seed from <see cref="GameplaySettingsDefinition"/>
        /// — same source the shared-agency path uses for first-universe initialization,
        /// so the per-agency mode inherits the operator's chosen career economy.
        ///
        /// Persists immediately via <see cref="SaveAgency"/> so a crash before the next
        /// periodic save doesn't lose the registration.
        ///
        /// No-op (returns null) when <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>
        /// is false.
        /// </summary>
        public static AgencyState RegisterAgency(string playerName)
        {
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
                return null;
            if (string.IsNullOrEmpty(playerName))
                throw new ArgumentException("Player name must be non-empty", nameof(playerName));

            // Per-name lock closes the same-name double-register race: two concurrent
            // OnPlayerAuthenticated for "Alice" arriving on separate receive threads
            // serialize here; the second call sees the first's commit and returns its
            // agency instead of minting a parallel one.
            lock (PlayerNameLocks.GetOrAdd(playerName, _ => new object()))
            {
                if (AgencyByPlayerName.TryGetValue(playerName, out var existingId))
                {
                    if (Agencies.TryGetValue(existingId, out var existing))
                        return existing;

                    // Stale-index defensive heal (round-2 review): the player-name index
                    // points at a Guid that's missing from the Agencies registry. Try the
                    // disk fallback (covers the .bak-recovery path); if that also misses,
                    // unbind the stale name so the mint path below runs cleanly instead of
                    // silently shadowing an orphan that lives forever in vessel stamps.
                    var healed = LoadAgency(existingId);
                    if (healed != null)
                    {
                        AgencyByPlayerName[playerName] = healed.AgencyId;
                        return healed;
                    }
                    AgencyByPlayerName.TryRemove(playerName, out _);
                    LunaLog.Warning($"[fix:per-agency-career] Healed stale AgencyByPlayerName entry for {playerName} (pointed at missing agency {existingId:N})");
                }

                var state = new AgencyState
                {
                    AgencyId = Guid.NewGuid(),
                    OwningPlayerName = playerName,
                    DisplayName = $"{playerName} Space Agency",
                    Funds = GameplaySettings.SettingsStore.StartingFunds,
                    Science = GameplaySettings.SettingsStore.StartingScience,
                    Reputation = GameplaySettings.SettingsStore.StartingReputation,
                };

                if (!Agencies.TryAdd(state.AgencyId, state))
                {
                    // Guid.NewGuid collision is astronomically unlikely; if it ever fires it
                    // indicates a deeper problem (deterministic GUID generator, test harness
                    // reuse, etc.) — fail loudly rather than silently overwrite an existing
                    // agency.
                    throw new InvalidOperationException(
                        $"GUID collision on AgencyId {state.AgencyId:N} while registering player {playerName}");
                }

                // Persistence-before-index ordering (round-2 review). The XML doc on
                // AgencyByPlayerName guarantees: persist via SaveAgency BEFORE flipping the
                // index. Prior order (flip-then-save) created a crash window between the
                // index write and the disk write where a server crash left the index pointing
                // at a GUID with no on-disk file. After restart, LoadExistingAgencies couldn't
                // find it; the player reconnected and minted a new agency; any vessel stamped
                // with the orphan GUID was disconnected from any active agency. By calling
                // SaveAgency first, a crash before the index flip simply means the player
                // reconnects, mints a new agency, and the orphan-on-disk is unreferenced and
                // safe to operator-delete. The vessel proto path (HandleVesselProto) reads
                // AgencyByPlayerName, so a crash here leaves vessels unstamped (Guid.Empty)
                // until next proto — also safe.
                SaveAgency(state.AgencyId);

                AgencyByPlayerName[playerName] = state.AgencyId;

                LunaLog.Normal($"[fix:per-agency-career] Registered new agency '{state.DisplayName}' ({state.AgencyId:N}) for player {playerName}");
                return state;
            }
        }

        /// <summary>
        /// Returns the agency for the given id. Registry-first; falls back to disk and
        /// caches in the registry on a hit. Returns null when the agency is unknown to
        /// both the registry and disk. Useful for admin tooling (Stage 5.18d) and for
        /// the future wire path where a client references an agency by id.
        ///
        /// No-op (returns null) when <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>
        /// is false.
        /// </summary>
        public static AgencyState LoadAgency(Guid agencyId)
        {
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
                return null;

            if (Agencies.TryGetValue(agencyId, out var existing))
                return existing;

            var filePath = FilePathFor(agencyId);
            var state = LoadAgencyFromFile(filePath);
            if (state == null)
                return null;

            if (Agencies.TryAdd(state.AgencyId, state) && !string.IsNullOrEmpty(state.OwningPlayerName))
                AgencyByPlayerName[state.OwningPlayerName] = state.AgencyId;

            return state;
        }

        /// <summary>
        /// Flushes one agency's state to disk via <see cref="FileHandler.WriteAtomic"/>.
        /// Caller is typically <see cref="RegisterAgency"/> (initial save) or — in future
        /// Stage 5 steps — a Share*-routed mutation. Holds the per-agency lock so
        /// <see cref="AgencyState.Serialize"/> doesn't race a concurrent field write.
        ///
        /// **Locking contract for callers that mutate <see cref="AgencyState"/> fields**
        /// (Stage 5.17b <c>Share*</c> writers): hold <see cref="GetAgencyLock"/> around
        /// the field write itself. <see cref="SaveAgency"/> serialises under the same
        /// lock, so the snapshot it persists is internally consistent. Without that,
        /// a multi-field mutation (e.g. paying for a tech node = debit Science + flip
        /// TechNodeState) can produce a torn intermediate snapshot on disk.
        ///
        /// No-op when <see cref="GameplaySettingsDefinition.PerAgencyCareer"/> is false.
        /// </summary>
        public static void SaveAgency(Guid agencyId)
        {
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
                return;
            if (!Agencies.TryGetValue(agencyId, out var state))
                return;

            lock (GetAgencyLock(agencyId))
            {
                FileHandler.WriteAtomic(FilePathFor(agencyId), state.Serialize());
            }
        }

        /// <summary>
        /// Called from <see cref="HandshakeSystem"/> when a client clears handshake.
        /// First-connect creates a fresh agency; subsequent connects return the existing
        /// one (idempotent — <see cref="RegisterAgency"/> handles both paths).
        ///
        /// No-op when <see cref="GameplaySettingsDefinition.PerAgencyCareer"/> is false.
        /// </summary>
        public static void OnPlayerAuthenticated(string playerName)
        {
            if (!GameplaySettings.SettingsStore.PerAgencyCareer)
                return;

            RegisterAgency(playerName);
        }

        /// <summary>
        /// Test-only helper. Clears the in-memory registries + lock anchors so successive
        /// tests don't carry state across. Called from <c>ServerTest</c> (<c>AgencySystemTest</c>)
        /// and <c>MockClientTest</c> (<c>ServerHarness.ResetPerTestState</c>); both assemblies
        /// have <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/> on
        /// the Server assembly. Never call from production code.
        /// </summary>
        internal static void Reset()
        {
            Agencies.Clear();
            AgencyByPlayerName.Clear();
            AgencyLocks.Clear();
            PlayerNameLocks.Clear();
        }

        /// <summary>
        /// Returns the per-agency lock object that <see cref="SaveAgency"/> acquires.
        /// Stage 5.17b <c>Share*</c> writers MUST hold this around field mutations on
        /// <see cref="AgencyState"/> so a concurrent SaveAgency does not serialise a
        /// torn multi-field snapshot. Same pattern as
        /// <see cref="Server.System.Scenario.ScenarioDataUpdater.GetSemaphore"/>.
        /// </summary>
        internal static object GetAgencyLock(Guid agencyId) =>
            AgencyLocks.GetOrAdd(agencyId, _ => new object());

        /// <summary>
        /// Reads + parses one agency file, healing the canonical path back from <c>.bak</c>
        /// if <see cref="FileHandler.ReadAtomic"/> had to fall back. The heal closes the
        /// 5.14c deferred CONSIDER where ReadAtomic would log the recovery warning on every
        /// subsequent read until something rewrote the canonical path.
        ///
        /// Returns null when the file (and its <c>.bak</c>) are absent — caller decides
        /// whether that means "skip" (LoadExistingAgencies) or "no such agency" (LoadAgency).
        ///
        /// **Locking (round-3 persistence review).** The heal <see cref="FileHandler.WriteAtomic"/>
        /// is gated on the per-agency lock when the parsed state has a real AgencyId, so a
        /// concurrent <see cref="SaveAgency"/> for the same agency cannot have its fresh
        /// content silently overwritten by stale <c>.bak</c> content during runtime
        /// <see cref="LoadAgency"/> calls. <see cref="LoadExistingAgencies"/> is boot-time
        /// (before the server accepts connections) so the lock is uncontested there but the
        /// guard is symmetric across both callers.
        /// </summary>
        private static AgencyState LoadAgencyFromFile(string filePath)
        {
            var canonicalExisted = FileHandler.FileExists(filePath);
            var bakExisted = FileHandler.FileExists(filePath + ".bak");

            var content = FileHandler.ReadAtomic(filePath);
            if (string.IsNullOrEmpty(content))
                return null;

            var state = AgencyState.Parse(content);

            if (!canonicalExisted && bakExisted)
            {
                // .bak supplied the content — write it back to the canonical path so
                // ReadAtomic stops surfacing the warning on every read after this one.
                // Hold the per-agency lock so a concurrent SaveAgency cannot land between
                // our content snapshot and the heal write and have its fresh state
                // silently rolled back to stale .bak. Stage 5.17b Share* writers will
                // need this guarantee — see GetAgencyLock locking contract above.
                lock (GetAgencyLock(state.AgencyId))
                {
                    FileHandler.WriteAtomic(filePath, content);
                }
                LunaLog.Normal($"[fix:per-agency-career] Healed canonical path after .bak recovery: {Path.GetFileName(filePath)}");
            }

            return state;
        }

        private static string FilePathFor(Guid agencyId) =>
            Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
    }
}
