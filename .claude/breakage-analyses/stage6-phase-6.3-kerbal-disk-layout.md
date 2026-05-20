# Stage 6 Phase 6.3 — kerbal disk layout + lifecycle hooks — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `449594c8` (docs(stage6): Stage 6 roadmap entry + Phase 6.1/6.2 stack notes + orphaned breakage analyses)
**Discipline:** Per `[[feedback-breakage-analysis]]`.
**Motivation:** Phase 6.3 lands the disk-layout foundation for Stage 6 per-agency kerbal roster. Adds two helpers + three lifecycle hooks. **Still no handler routing.** `HandleKerbalsRequest` / `HandleKerbalProto` / `HandleKerbalRemove` are untouched — Phases 6.4 / 6.5 land routing. Phase 6.3 is observably silent under the default `PerAgencyKerbalRoster=false`; under gate=on every freshly-minted or loaded agency gets its own seeded `Kerbals/` subdir + every `/deleteagency` removes that subdir.

---

## Scope lock — IS

### 1. `Server/System/FileHandler.cs` — new `FolderDeleteRecursive` helper

The existing `FolderDelete(path)` wraps `Directory.Delete(path)` (non-recursive) and throws on non-empty directories. Phase 6.3 needs to remove `Universe/Agencies/{guid:N}/Kerbals/` which always contains files. Add a sibling helper that wraps `Directory.Delete(path, recursive: true)` under the same per-path lock semaphore:

```csharp
/// <summary>
/// Thread safe RECURSIVE folder delete. Equivalent to
/// <see cref="FolderDelete(string)"/> but passes <c>recursive: true</c> to
/// <see cref="Directory.Delete(string, bool)"/>. Required for Stage 6's
/// per-agency Kerbals subdir cascade on <c>/deleteagency</c>.
/// </summary>
public static void FolderDeleteRecursive(string path)
{
    lock (GetLockSemaphore(path))
    {
        Directory.Delete(path, recursive: true);
    }
}
```

Caller is responsible for `FolderExists` check before invoking — matches the existing `FolderDelete` contract (which also throws on missing path).

### 2. `Server/System/Agency/AgencySystem.cs` — `GetKerbalsPathForAgency` helper

Public expression-bodied so `ServerContext.UniverseDirectory` mutations (ServerTest per-test temp dirs) re-resolve correctly. Mirrors `KerbalSystem.KerbalsPath` and `AgencyState.AgenciesPath`:

```csharp
/// <summary>
/// [Stage 6] Path to the per-agency kerbal subdir for the given agency:
/// <c>Universe/Agencies/{agencyId:N}/Kerbals/</c>. Sibling of the agency
/// state file at <c>Universe/Agencies/{agencyId:N}.txt</c> — directory and
/// file share the same stem under <see cref="AgencyState.AgenciesPath"/>,
/// which is fine on every supported filesystem (a folder named "foo" and a
/// file named "foo.txt" coexist cleanly).
///
/// Phase 6.3 lifecycle hooks ensure this directory exists with seeded
/// stock 4 (Jeb/Bill/Bob/Val) for every freshly-minted or loaded agency
/// under <see cref="PerAgencyEnabled"/>. Phase 6.4/6.5 handlers route
/// kerbal reads/writes through this path under
/// <see cref="PerAgencyKerbalRosterEnabled"/>.
///
/// Expression-bodied (not <c>static readonly</c>) so per-test temp
/// <see cref="ServerContext.UniverseDirectory"/> rewrites flow through.
/// </summary>
public static string GetKerbalsPathForAgency(Guid agencyId)
    => Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N", CultureInfo.InvariantCulture), "Kerbals");
```

### 3. `Server/System/Agency/AgencySystem.cs` — `SeedStockKerbalsForAgency` helper

Mirrors the shape of `EnsureStartTechSeeded` — returns `bool` so the caller can collapse multiple seed steps into one persistence step (though here the persistence is per-file via `FileHandler.WriteAtomic`, not via `SaveAgency` — there is no AgencyState mutation). Idempotent per-file via `FileHandler.FileExists` check.

```csharp
/// <summary>
/// [Stage 6 / Phase 6.3] Seeds the four stock kerbals (Jebediah / Bill /
/// Bob / Valentina) into the agency's <see cref="GetKerbalsPathForAgency"/>
/// subdir from the embedded <see cref="Server.Properties.Resources"/>
/// templates. Mirrors <see cref="EnsureStartTechSeeded"/> in shape +
/// idempotency contract — safe to call multiple times.
///
/// <para><b>Per-file idempotency.</b> Skips any kerbal file that already
/// exists in the target subdir. Partial-seed recovery (operator manually
/// deleted 1 of 4 files) automatically re-seeds the missing names on next
/// invocation. Same per-name template each time — no name-collision
/// considerations because Q-Seed (spec §2) chose deterministic
/// same-name-across-agencies (each agency's "Jebediah Kerman" is its own
/// independent copy).</para>
///
/// <para><b>Persistence path.</b> Each kerbal file written via
/// <see cref="FileHandler.WriteAtomic"/> — Stage 6 spec §3 calls for
/// atomic writes on per-agency kerbal files because under gate=on each
/// file is the ONLY copy of that agency's version of that kerbal (no
/// shared default to fall back on). Mirrors AgencyState's
/// <see cref="FileHandler.WriteAtomic"/> usage.</para>
///
/// <para><b>Subdir creation.</b> <see cref="FileHandler.WriteAtomic"/>
/// handles parent-directory creation via its existing
/// <see cref="Directory.CreateDirectory"/> contract — no separate
/// <see cref="FileHandler.FolderCreate"/> call needed before the seed
/// loop.</para>
///
/// <para><b>Returns</b> <c>true</c> when at least one file was created;
/// <c>false</c> on every no-op branch (gate closed / all 4 already
/// present). Caller uses the bool to decide whether to emit a log line.</para>
///
/// No-op when <see cref="PerAgencyEnabled"/> is false (gate off OR
/// non-Career). Independent of <see cref="PerAgencyKerbalRosterEnabled"/>
/// at seed time — the disk layout is established up-front for every
/// agency, and the rosters become observably-per-agency only when the
/// handler routing turns on in Phase 6.4/6.5. Pre-seeding under
/// PerAgencyCareer-only mode is benign because nothing reads the subdir
/// yet; seeding gated on the combined predicate would mean operators
/// who flip PerAgencyKerbalRoster=true mid-cohort would need a
/// per-agency backfill pass we'd then have to write.
/// </summary>
private static bool SeedStockKerbalsForAgency(AgencyState state)
{
    if (state == null || !PerAgencyEnabled)
        return false;

    var kerbalsDir = GetKerbalsPathForAgency(state.AgencyId);

    var templates = new (string FileName, string Content)[]
    {
        ("Jebediah Kerman.txt", Server.Properties.Resources.Jebediah_Kerman),
        ("Bill Kerman.txt",     Server.Properties.Resources.Bill_Kerman),
        ("Bob Kerman.txt",      Server.Properties.Resources.Bob_Kerman),
        ("Valentina Kerman.txt", Server.Properties.Resources.Valentina_Kerman),
    };

    var seeded = 0;
    foreach (var (fileName, content) in templates)
    {
        var path = Path.Combine(kerbalsDir, fileName);
        if (FileHandler.FileExists(path))
            continue;
        FileHandler.WriteAtomic(path, content);
        seeded++;
    }

    if (seeded > 0)
    {
        LunaLog.Normal(
            $"[fix:per-agency-kerbal-disk-layout] Seeded {seeded} stock kerbal file(s) into agency " +
            $"{state.AgencyId:N} ({state.OwningPlayerName}) at {kerbalsDir}");
    }
    return seeded > 0;
}
```

### 4. Wire `SeedStockKerbalsForAgency` into `RegisterAgency`

Inserted immediately after the existing `EnsureStartTechSeeded(state);` call (line 1879). Order matches the spec §3 lifecycle list:

```csharp
EnsureStartTechSeeded(state);
SeedStockKerbalsForAgency(state);
SaveAgency(state.AgencyId);
```

No defensive ordering needed against `SaveAgency` — kerbal seeding mutates the disk only (separate files per kerbal), not `AgencyState`. The `SaveAgency` call below remains the single persistence step for the AgencyState fields.

### 5. Wire `SeedStockKerbalsForAgency` into `LoadAgencyFromFile`

Inserted immediately after the existing `EnsureStartTechSeeded` backfill block (line ~2705). Mirrors the start-tech-seed pattern but does NOT need the inline `WriteAtomic(filePath, state.Serialize())` companion — kerbal files are their own disk entries, not AgencyState fields. The seed helper already does `WriteAtomic` per kerbal file internally.

```csharp
if (EnsureStartTechSeeded(state))
{
    lock (GetAgencyLock(state.AgencyId))
    {
        FileHandler.WriteAtomic(filePath, state.Serialize());
    }
    LunaLog.Normal(/* ... */);
}

// [fix:per-agency-kerbal-disk-layout, Stage 6 Phase 6.3] Backfill the
// stock kerbal seed for pre-Stage-6 agency files. No-op when the agency
// already has all 4 files OR when PerAgencyEnabled is false. Helper
// persists per-kerbal-file via FileHandler.WriteAtomic internally —
// no AgencyState mutation, so no companion WriteAtomic on filePath.
SeedStockKerbalsForAgency(state);

return state;
```

### 6. Wire kerbal-subdir cascade-delete into `TryDeleteAgency`

Inserted after the canonical-file delete + .bak delete block (lines 2189-2191). The new block deletes both the per-agency kerbal subdir AND its parent `{guid:N}/` folder if the parent ends up empty (defensive — Phase 6.3 only creates `Kerbals/` under it, but future Stage-6+ work may add sibling subdirs that we'd want to preserve if non-empty).

```csharp
var canonicalPath = source.FilePath;
FileHandler.FileDelete(canonicalPath);
FileHandler.FileDelete(canonicalPath + ".bak");

// [fix:per-agency-kerbal-disk-layout, Stage 6 Phase 6.3] Cascade-delete
// the agency's per-agency Kerbals subdir if present. Pre-Stage-6
// agencies have no subdir (the FolderExists guard handles that). The
// parent {guid:N}/ folder is preserved if it has siblings (future
// Stage-6+ subdirs); deleted as empty parent here. FolderDeleteRecursive
// throws on partial filesystem failures; let it propagate so the caller
// sees the inconsistency rather than silently leaving orphan files.
var kerbalsDir = GetKerbalsPathForAgency(agencyId);
if (FileHandler.FolderExists(kerbalsDir))
{
    var kerbalFileCount = FileHandler.GetFilesInPath(kerbalsDir).Length;
    FileHandler.FolderDeleteRecursive(kerbalsDir);
    LunaLog.Normal(
        $"[fix:per-agency-kerbal-disk-layout] Deleted {kerbalFileCount} per-agency kerbal file(s) for agency " +
        $"{agencyId:N} ({oldOwnerName}) at {kerbalsDir}");
}
var agencyDir = Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N", CultureInfo.InvariantCulture));
if (FileHandler.FolderExists(agencyDir) && !Directory.EnumerateFileSystemEntries(agencyDir).Any())
{
    FileHandler.FolderDelete(agencyDir);
}
```

### 7. ForkBuildInfo entry

Append `per-agency-kerbal-disk-layout` to `Server/ForkBuildInfo.cs:ActiveFixes[]`. Operators see the new fix in the boot banner. Entry text: "Stage 6 Phase 6.3 — per-agency kerbal subdir + stock-4 seeding + delete-cascade. No handler routing yet (Phase 6.4/6.5)."

### 8. ServerTest cases

New `ServerTest/PerAgencyKerbalDiskLayoutTest.cs` (~9 tests, ~330 lines). Setup/Teardown mirrors `AgencyStartTechSeedingTest`:

1. **`GetKerbalsPathForAgency_ResolvesUnderUniverseDirectory`** — temp-dir-based test; asserts the path composes correctly + re-resolves when UniverseDirectory changes mid-test (the expression-bodied contract).
2. **`RegisterAgency_UnderPerAgencyEnabled_SeedsFourStockKerbalFiles`** — full path through RegisterAgency. Asserts subdir exists, contains exactly 4 files with the expected names + non-zero byte content.
3. **`SeedStockKerbalsForAgency_Idempotent`** — call helper directly twice; asserts no duplicate files + second call returns false.
4. **`SeedStockKerbalsForAgency_PartialSeedRecovery`** — manually delete 1 of the 4 files between seed calls; assert second call returns true + restores the missing file.
5. **`SeedStockKerbalsForAgency_GateOff_NoOp`** — `PerAgencyCareer=false` → seed returns false, no files, no subdir.
6. **`LoadAgencyFromFile_BackfillsKerbalsForPreStage6Agency`** — write an AgencyState file directly to disk WITHOUT a Kerbals subdir; call `LoadAgency` (which internally calls `LoadAgencyFromFile`); assert subdir + 4 files appear after the load.
7. **`TryDeleteAgency_RemovesKerbalsSubdir`** — RegisterAgency to populate; TryDeleteAgency; assert the Kerbals subdir + the parent `{guid:N}/` folder are both gone.
8. **`TryDeleteAgency_NoKerbalsSubdir_DoesNotThrow`** — Manually delete the Kerbals subdir between Register + TryDelete (simulating pre-Stage-6 or operator interference); assert TryDeleteAgency succeeds.
9. **`SeedStockKerbalsForAgency_FileContentParsesAsConfigNode`** — seed Jeb; read back from disk; assert the ConfigNode parse succeeds + the kerbal name extracted matches "Jebediah Kerman". This guards the stock-template bytes against accidental corruption (the embedded resource bytes round-trip through LunaConfigNode cleanly).

ServerTest delta: 682 → ~691 (+9).

### 9. CLAUDE.md updates

Defer to the wrap-up step (separate non-code commit alongside memory updates). Phase 6.3 doesn't change wire shape or runtime behaviour under default settings. Stage Roadmap entry updated to mark 6.3 complete + brief Stack Notes entry on the subdir-coexists-with-sibling-file disk layout decision.

---

## Scope lock — IS NOT

- **No handler routing.** `HandleKerbalsRequest`, `HandleKerbalProto`, `HandleKerbalRemove` are untouched. They still write to `KerbalSystem.KerbalsPath` (shared) regardless of the new gate. Phase 6.4/6.5 lands routing.
- **No K1 guard changes.** `CanRemoveKerbalUnderK1` stays. Becomes structurally moot under gate=on once Phase 6.4/6.5 ship; removal is Stage 7.
- **No client-side change.** Server scaffolding + disk only. Client-side label formatter is Phase 6.6.
- **No wire-protocol change.** Protocol stays 0.31.0. No new MsgData. No SubTypeDictionary additions.
- **No `/setvesselagency` crew migration.** Phase 6.8.
- **No WOLF Slice F cascade update.** Phase 6.7.
- **No Final Frontier support.** Phase 6.9 (optional).
- **No combined-gate composition in this phase's helpers.** `SeedStockKerbalsForAgency` gates on `PerAgencyEnabled` (not `PerAgencyKerbalRosterEnabled`) so the disk layout is pre-established for every per-agency-career universe regardless of whether the operator has flipped the kerbal-roster setting yet. Documented in the helper's XML — rationale is "no migration pass needed if the operator flips the roster setting mid-cohort." Cost is ~4kB per agency of unused-but-correct stock kerbal files (negligible) under PerAgencyKerbalRoster=false.
- **No edits to existing `EnsureStartTechSeeded`.** Parallel sibling, same shape.
- **No FileHandler refactor beyond the single `FolderDeleteRecursive` addition.** The existing `FolderDelete` stays as-is — callers that need non-recursive (none currently) keep working.

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| Fresh agency, gate=on | RegisterAgency mints subdir + 4 files. Single `[fix:per-agency-kerbal-disk-layout]` Normal log. |
| Fresh agency, gate=off (PerAgencyCareer=false) | Seed helper short-circuits before any IO. Same behaviour as v7. |
| Pre-Stage-6 agency file (no `Kerbals/` subdir on disk), gate=on | `LoadAgencyFromFile` backfills via `SeedStockKerbalsForAgency`. Subdir + 4 files appear. Single Normal log. |
| Pre-Stage-6 agency file, gate=off | Backfill helper short-circuits. v7 behaviour. |
| Agency with partial seed (operator deleted 1 file mid-cohort) | Next `LoadAgencyFromFile` re-seeds the missing file only. Idempotent per-file check. |
| Agency with all 4 files already present | Backfill returns false. Zero log, zero disk write. |
| `/deleteagency` on pre-Stage-6 agency (no subdir) | `FolderExists` guard fires false. Cascade-delete block is a no-op. Existing canonical-file delete still works. |
| `/deleteagency` on Stage-6-minted agency | Subdir + 4 files removed. Parent `{guid:N}/` folder removed (now empty). `{guid:N}.txt` removed as before. |
| `/deleteagency` mid-cohort where operator manually added a non-Kerbals subdir under `{guid:N}/` | `Kerbals/` removed; parent `{guid:N}/` preserved because `Directory.EnumerateFileSystemEntries` finds the operator's content. |
| Concurrent register + delete on same player (registered + immediately purged) | Per-agency lock + per-name lock serialize the two ops. The seed runs under RegisterAgency's lock scope; the cascade runs under TryDeleteAgency's. No cross-block races. |
| Disk full during seed | `FileHandler.WriteAtomic` propagates `IOException`. RegisterAgency's caller (HandshakeSystem) sees the throw + treats the auth as failed. Acceptable failure mode — no half-mint state because the AgencyState `SaveAgency` runs AFTER the seed. |
| Disk full during one of the 4 seed files (partial seed) | First-loop iterations succeed, the failing iteration throws, the caller fails. Next reconnect → next RegisterAgency call → seed completes the missing files via partial-seed-recovery branch. Self-healing. |
| Operator hand-deletes `Universe/Agencies/{guid:N}/` between seed + first handler routing (Phase 6.4/6.5) | Phase 6.3 doesn't observe this. Phase 6.4 handler will see an empty `Kerbals/` reply; under Phase 6.5 routing, the FIRST KerbalProto from the player will re-create the file via the standard write path. No cascading failure. |
| ServerTest temp-dir teardown | `Directory.Delete(ServerContext.UniverseDirectory, recursive: true)` already handles the new subdir tree. No teardown change needed. |
| Subdir vs sibling-file name collision (`{guid:N}` folder + `{guid:N}.txt` file) | Filesystem-supported on Windows + Linux. Demonstrated by existing CRP / MKS file layouts that mix this. |
| Resource template content has CRLF vs LF mismatch with `WriteAtomic` | `Resources.*_Kerman` are embedded as raw string constants. Whatever line ending the resx generator chose is what gets written. Existing `KerbalSystem.GenerateDefaultKerbals` uses `FileHandler.CreateFile` (also raw write) without issue, so the bytes are KSP-readable as-is. |

---

## Failure modes considered

| Mode | Mitigation |
|------|------------|
| `FolderDeleteRecursive` throws on a locked subdir (Windows file handle held by AV scanner) | Throws to caller. TryDeleteAgency catches at the call site? **Currently no** — let me check: TryDeleteAgency is wrapped in a single `try` around the registry-mutation block but the cascade comes after. If `FolderDeleteRecursive` throws, the canonical file IS already deleted, the registry IS already cleared, but the subdir survives as an orphan. Mitigation: the next `LoadExistingAgencies` won't see the orphan because there's no `.txt` for it; the operator can hand-delete the directory. **Acceptable: orphan state is observable, not data-destroying.** Logged at WARN if we wrap the call in try/catch with continue-on-fail; left to throw if we want the operator to see the IOException stack. Default to **throw** — operator visibility outweighs cleanup completeness, mirroring `FileHandler.FileDelete`'s let-it-throw contract. |
| `WriteAtomic` fails mid-seed (4 files attempted, 1 fails) | RegisterAgency's caller (HandshakeSystem) sees the throw. Agency state has NOT been saved yet (SaveAgency runs after seed). Player reconnects → RegisterAgency runs again → partial-seed-recovery completes the missing file → SaveAgency persists state. Self-healing. |
| Resource template missing at compile time | Compile-time failure (CS0117 on `Resources.Jebediah_Kerman` etc.). Caught before merge. The 4 resources already ship in `Server/Properties/Resources.resx`. |
| Pre-Stage-6 agency file loaded under gate=on with `Universe/Kerbals/` populated + boot-refusal flag false | Phase 6.2's `RefuseStartupIfKerbalHazardWithoutOverride` already blocks startup. Phase 6.3 backfill never runs because the server doesn't start. |
| Pre-Stage-6 agency loaded under gate=on with override=true | Backfill runs. Per-agency kerbals appear in the subdir. Legacy `Universe/Kerbals/` stays untouched (frozen reference set per spec §Q-Migration). Phase 6.4/6.5 routing will route reads to the subdir, never to the legacy dir. Phase 6.3 by itself is silent because handlers don't read the gate yet. |
| Existing `KerbalSystem.GenerateDefaultKerbals` runs at universe init | Unchanged. Writes to `Universe/Kerbals/` (shared). Phase 6.3 adds a NEW per-agency path that coexists. Under PerAgencyKerbalRoster=false, this shared dir stays authoritative (handlers point at it). Under gate=on (post Phase 6.4/6.5), the shared dir is the frozen reference set per Q-Migration. |
| TryDeleteAgency's parent-folder empty-check race | `Directory.EnumerateFileSystemEntries` enumerates lazily; if a concurrent process adds a file between the empty-check and `FolderDelete`, the delete throws `IOException("Directory not empty")`. Single-process server, so no concurrent process; acceptable. |
| Path length on Windows with long agency-id paths | `{guid:N}` is 32 chars; `Universe/Agencies/{32-chars}/Kerbals/Valentina Kerman.txt` is well under MAX_PATH. No mitigation needed. |
| File-system case sensitivity on Linux | Stock kerbal filenames are fixed ASCII; not user-input. Round-trips identically. |
| Operator manually edits a kerbal file under `Universe/Agencies/{guid:N}/Kerbals/{name}.txt` between mint and load | Phase 6.3 backfill is per-file `FileExists`-gated; operator edits survive (file exists, skip seed). Operator edits to the 4 seeded names are preserved. Operator edits to additional names (e.g. operator pre-seeds a 5th kerbal) are likewise preserved. |

---

## Multi-lens review plan

After implementation + tests pass, run **two parallel lenses** per `[[feedback-review-lens-framing]]` + `[[feedback-rebuild-before-claiming-green]]`:

1. **server-systems-review** — confirm: helper composition; idempotency contract; lifecycle hook ordering (seed before SaveAgency vs after); cascade-delete failure modes; FolderDeleteRecursive lock-contract matches sibling helpers; XML doc accuracy; `[fix:per-agency-kerbal-disk-layout]` tag consistency.
2. **persistence-review** — confirm: WriteAtomic vs WriteToFile choice rationale matches spec §3; per-file lock granularity (each `WriteAtomic` takes its own per-path lock — no AgencyState lock needed); cascade-delete ordering vs in-flight writes; resource template content integrity (no CRLF vs LF surprises that would break a KSP-side reload).

Expect 0 MUST FIX (small surface, established precedent — the shape mirrors `EnsureStartTechSeeded` + the existing TryDeleteAgency canonical-file unlink + the `AgencyState.AgenciesPath` path-construction pattern). Any SHOULD FIX gets folded into the same commit before review-receipt.

---

## Test surface delta

| Suite | Pre | Post | Delta |
|-------|-----|------|-------|
| ServerTest | 682 | ~691 | +9 (path helper + register-mint + idempotency + partial-recovery + gate-off + load-backfill + delete-cascade + delete-no-subdir + content-parses) |
| LmpClientTest | 165 | 165 | 0 (no client change) |
| MockClientTest | ~100 | ~100 | 0 (no wire change) |
| LmpCommonTest | 14 | 14 | 0 |

---

## Commit metadata

- **Branch**: `feature/per-agency`
- **Commit subject**: `feat(server,agency): Stage 6 Phase 6.3 — per-agency kerbal subdir + stock-4 seeding + delete cascade`
- **Scope token**: `server,agency` (per CLAUDE.md allowed scopes)
- **No AI attribution** (silent partner rule)
- **Review receipt**: `.claude/review-receipts/{sha1}.txt` required by `require-bug-review.sh` PreToolUse hook
