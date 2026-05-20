# Stage 6 Phase 6.2 — settings + gate + boot-refusal scaffolding — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `64d5f2e1` (docs(stage6): land per-agency kerbal roster spec with pre-implementation gap amendments)
**Discipline:** Per `[[feedback-breakage-analysis]]`.
**Motivation:** Phase 6.2 lands the gate and boot-refusal scaffolding for Stage 6's per-agency kerbal roster — settings round-trip, combined-gate predicate, hazard-warn predicate, fail-closed boot-refusal predicate, and ForkBuildInfo banner entry. **Gate defaults false; handlers do NOT read the gate yet** (handler routing is Phase 6.4/6.5). The scaffolding is observably silent unless `PerAgencyKerbalRoster=true` is opted into.

---

## Scope lock — IS

### 1. Two new settings in `Server/Settings/Definition/GameplaySettingsDefinition.cs`

Added next to the existing `PerAgencyCareer` + `AllowEnablePerAgencyOnExistingUniverse` pair (lines 63-67), keeping the related gate-pair grouping intact:

```csharp
[XmlComment(Value = "[Stage 6] Enable per-agency kerbal roster (each agency has its own Jeb/Bill/Bob/Val + recruits/EVA rescues/deaths). Default false preserves shared-roster behaviour. Requires PerAgencyCareer=true; has no effect under shared-agency career mode. Cannot be changed mid-save: pick a value before the universe is first populated.")]
public bool PerAgencyKerbalRoster { get; set; }

[XmlComment(Value = "[Stage 6] Allow PerAgencyKerbalRoster=true on a universe that already has accumulated shared kerbals in Universe/Kerbals/. Default false: server refuses to start with a loud message pointing operator to the fresh-start workflow (spec §10 Q-Migration). Set true ONLY if you accept that the legacy shared kerbals become a frozen reference set (each agency mints fresh stock 4; no migration).")]
public bool AllowEnablePerAgencyKerbalsOnExistingUniverse { get; set; }
```

Update each of the 4 difficulty presets (`SetEasy/Normal/Moderate/Hard`) to set both to false alongside the existing `PerAgencyCareer = false; AllowEnablePerAgencyOnExistingUniverse = false;` lines. Matches the per-agency-career precedent — these are gameplay-mode toggles orthogonal to KSP's difficulty curve so they always default false regardless of difficulty.

### 2. New combined gate predicate in `Server/System/Agency/AgencySystem.cs`

Added next to the existing `PerAgencyEnabled` predicate (line 61):

```csharp
/// <summary>
/// [Stage 6] Combined gate for per-agency kerbal roster handlers. Composes the
/// existing <see cref="PerAgencyEnabled"/> (PerAgencyCareer + Career mode)
/// AND the dedicated <see cref="GameplaySettingsDefinition.PerAgencyKerbalRoster"/>
/// setting. Per Stage 6 spec §1, per-agency kerbal roster REQUIRES per-agency
/// career as a precondition — the kerbal partition leans on
/// <see cref="AgencyByPlayerName"/> for sender→agency resolution, which is only
/// populated when the career gate is on.
///
/// Phase 6.2 lands this predicate but no handler reads it yet — handler
/// routing is Phase 6.4 (HandleKerbalsRequest filter) and Phase 6.5
/// (HandleKerbalProto + HandleKerbalRemove routing).
/// </summary>
public static bool PerAgencyKerbalRosterEnabled =>
    PerAgencyEnabled &&
    GameplaySettings.SettingsStore.PerAgencyKerbalRoster;
```

### 3. New `WarnAboutSharedKerbalsOnUpgrade()` diagnostic in `AgencySystem.cs`

Mirrors the established narrow-hazard predicate shape used by `WarnAboutSharedSCANsatOnUpgrade` and `WarnAboutSharedDMagicOnUpgrade`. Differs in that the hazard predicate does NOT depend on `Agencies.Count > 0` or `VesselStoreSystem.CurrentVessels.IsEmpty` — the kerbal hazard is about a directly observable disk condition (legacy `Universe/Kerbals/` has files AND the gate is being flipped on), independent of how mature the per-agency setup is.

```csharp
/// <summary>
/// [Stage 6 upgrade-lens diagnostic] Fires when (a) PerAgencyKerbalRoster=true
/// AND (b) Universe/Kerbals/ contains kerbal files. Under those conditions,
/// Phase 6.4's HandleKerbalsRequest filter will route each player to their
/// per-agency Universe/Agencies/{guid}/Kerbals/ subdir; the legacy shared
/// kerbals become a frozen reference set (no agency reads or mutates them).
///
/// Per spec §Q-Migration, the operator workflow is fresh-start: archive
/// Universe/Kerbals/ before flipping the gate. The opt-in flag
/// AllowEnablePerAgencyKerbalsOnExistingUniverse=true accepts the frozen-
/// reference outcome explicitly.
///
/// Independent of the career-side hazard family — kerbal-roster migration
/// is its own decision orthogonal to career-scalar migration. An operator
/// may have already accepted career migration via
/// AllowEnablePerAgencyOnExistingUniverse=true but still need to accept the
/// kerbal migration separately.
/// </summary>
private static void WarnAboutSharedKerbalsOnUpgrade()
{
    if (!GameplaySettings.SettingsStore.PerAgencyKerbalRoster) return;
    var legacyDir = KerbalSystem.KerbalsPath;
    if (!FileHandler.FolderExists(legacyDir)) return;
    var legacyFiles = FileHandler.GetFilesInPath(legacyDir)
        .Where(p => Path.GetExtension(p) == ".txt").ToArray();
    if (legacyFiles.Length == 0) return;

    LunaLog.Warning(
        $"[fix:per-agency-kerbal-roster-scaffolding] PerAgencyKerbalRoster=true on a universe with " +
        $"{legacyFiles.Length} legacy shared kerbal file(s) at {legacyDir}. " +
        "Under Stage 6 per-agency mode, each agency reads/writes only its own Universe/Agencies/" +
        "{agencyId}/Kerbals/ subdir — the legacy shared kerbals stay readable on disk but no agency " +
        "can mutate them; they become a frozen reference set. " +
        "RECOVERY OPTIONS: " +
        "(1) Fresh-start workflow (spec §Q-Migration): stop the server, archive Universe/Kerbals/, " +
        "restart. Each agency mints its own Jeb/Bill/Bob/Val from stock templates. " +
        "(2) Accept the frozen-reference outcome + set " +
        "AllowEnablePerAgencyKerbalsOnExistingUniverse=true in Settings/GameplaySettings.xml. " +
        "Each agency still mints fresh stock 4; the legacy files remain readable for operator " +
        "inspection but are unowned. " +
        "(3) Stay on shared-roster mode (PerAgencyKerbalRoster=false) — no change.");
}
```

Wired into `LoadExistingAgencies()` alongside the other `WarnAbout*` helpers — both the empty-folder branch (line ~209) AND the populated-folder branch (line ~365). Phase 6.2 wires it at both branches so the operator sees the diagnostic whether or not any per-agency state has loaded — the kerbal hazard is independent of the career hazard family.

### 4. New `RefuseStartupIfKerbalHazardWithoutOverride()` in `AgencySystem.cs`

```csharp
/// <summary>
/// [Stage 6 boot-refusal hardening] Mirrors
/// <see cref="RefuseStartupIfUpgradeHazardWithoutOverride"/> but for the
/// kerbal-roster-specific override. Detects the upgrade-in-place hazard
/// (PerAgencyKerbalRoster=true + legacy Universe/Kerbals/ has files) and
/// flips <see cref="ServerContext.ServerRunning"/> to false unless
/// <see cref="GameplaySettingsDefinition.AllowEnablePerAgencyKerbalsOnExistingUniverse"/>
/// is true.
///
/// Independent of <see cref="RefuseStartupIfUpgradeHazardWithoutOverride"/> —
/// the two overrides are orthogonal. An operator can accept the career-
/// scalar projection-strip without accepting the kerbal-roster frozen-
/// reference outcome, and vice versa.
/// </summary>
private static void RefuseStartupIfKerbalHazardWithoutOverride()
{
    if (GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse)
        return; // Operator opted in; respect the override.
    if (!GameplaySettings.SettingsStore.PerAgencyKerbalRoster)
        return; // Gate off; no hazard.

    var legacyDir = KerbalSystem.KerbalsPath;
    if (!FileHandler.FolderExists(legacyDir)) return;
    var legacyFiles = FileHandler.GetFilesInPath(legacyDir)
        .Where(p => Path.GetExtension(p) == ".txt").ToArray();
    if (legacyFiles.Length == 0) return;

    LunaLog.Fatal(
        $"[fix:per-agency-kerbal-roster-scaffolding] BOOT REFUSED: PerAgencyKerbalRoster=true on a " +
        $"universe with {legacyFiles.Length} legacy shared kerbal file(s) at {legacyDir}. " +
        "Per spec §Q-Migration, the server fails closed by default — Phase 6.4's per-agency request " +
        "filter would leave the legacy shared kerbals as a frozen reference set readable but " +
        "unowned by any agency. Resolve by either: (a) follow spec §Q-Migration fresh-start workflow " +
        "— stop server, archive Universe/Kerbals/, restart with each agency minting its own stock 4; " +
        "OR (b) set AllowEnablePerAgencyKerbalsOnExistingUniverse=true in Settings/GameplaySettings.xml " +
        "to explicitly accept the frozen-reference outcome and continue. The server will now shut down.");
    ServerContext.ServerRunning = false;
}
```

Wired into `LoadExistingAgencies()` alongside the existing `RefuseStartupIfUpgradeHazardWithoutOverride()` call at lines 210 and 387.

### 5. ForkBuildInfo entry

Append `per-agency-kerbal-roster-scaffolding` to `Server/ForkBuildInfo.cs:ActiveFixes[]` so operators see the scaffolding active in the boot banner. The entry text explains: settings + gate + boot-refusal landed; no handler routing yet; phase 6.4/6.5 will activate the runtime path.

### 6. ServerTest cases

New `ServerTest/Agency/PerAgencyKerbalRosterScaffoldingTest.cs` (~6 tests, ~250 lines):

1. **`Settings_RoundTrip_PreservesPerAgencyKerbalRoster`** — Writes a `GameplaySettingsDefinition` with `PerAgencyKerbalRoster=true` + `AllowEnablePerAgencyKerbalsOnExistingUniverse=true`, round-trips through `SettingsHandler.SaveSettings` + `LoadSettings`, asserts both values preserved.
2. **`PerAgencyKerbalRosterEnabled_RequiresPerAgencyCareer`** — gate=on alone (PerAgencyCareer=false + PerAgencyKerbalRoster=true) → `PerAgencyKerbalRosterEnabled` returns false. Demonstrates the AND-composition.
3. **`PerAgencyKerbalRosterEnabled_RequiresCareerGameMode`** — PerAgencyCareer=true + PerAgencyKerbalRoster=true + GameMode=Sandbox → `PerAgencyKerbalRosterEnabled` returns false (inherits `PerAgencyEnabled`'s career-mode-only constraint).
4. **`PerAgencyKerbalRosterEnabled_AllGatesOn_ReturnsTrue`** — full happy path: all three (PerAgencyCareer + PerAgencyKerbalRoster + Career mode) returns true.
5. **`BootRefusal_FiresOnLegacyKerbalsWithoutOverride`** — PerAgencyKerbalRoster=true + populated `Universe/Kerbals/` + `AllowEnablePerAgencyKerbalsOnExistingUniverse=false` → `RefuseStartupIfKerbalHazardWithoutOverride` flips `ServerContext.ServerRunning = false`.
6. **`BootRefusal_BypassedByOverride`** — same scenario but with `AllowEnablePerAgencyKerbalsOnExistingUniverse=true` → boot does NOT refuse.

Plus 1 negative case for the Warn helper:

7. **`WarnAboutSharedKerbalsOnUpgrade_GateOff_NoLog`** — `PerAgencyKerbalRoster=false` → helper short-circuits before scanning the directory (defensive zero-IO path).

### 7. CLAUDE.md updates

Defer to the wrap-up step (separate non-code commit alongside memory updates). Phase 6.2 doesn't change runtime semantics or wire shape, so the Stage Roadmap entry can land with the memory update.

---

## Scope lock — IS NOT

- **No handler routing yet.** `HandleKerbalsRequest`, `HandleKerbalProto`, `HandleKerbalRemove` are untouched. They still write to `KerbalSystem.KerbalsPath` (shared) regardless of the new gate. Phase 6.4/6.5 lands the routing.
- **No `AgencySystem.GetKerbalsPathForAgency` helper yet.** Phase 6.3 lands it alongside `SeedStockKerbalsForAgency` + lifecycle hooks.
- **No agency subdir creation at mint.** Same — Phase 6.3.
- **No stock-4 seeding.** Phase 6.3.
- **No `/setvesselagency` crew migration.** Phase 6.8.
- **No client-side change.** Server scaffolding only.
- **No new wire types or protocol bump.** Protocol stays 0.31.0; the new settings ship in `SettingsReplyMsgData` via the existing XML-serialized `ServerParameters` envelope (no schema change required because `XmlSerializer` round-trips new bool fields cleanly — the same path already carries `PerAgencyCareer` + `AllowEnablePerAgencyOnExistingUniverse`).
- **No edits to existing `RefuseStartupIfUpgradeHazardWithoutOverride`.** New parallel function with orthogonal override.
- **No FF / Kerbalism / TAC-LS support.** Spec §9 out-of-scope explicitly.

---

## Edge cases

| Case | Behaviour |
|------|-----------|
| Fresh universe, gate=off (default) | Helper short-circuits; predicate returns false; boot-refusal short-circuits. Zero behavior change vs v7. |
| Fresh universe, PerAgencyKerbalRoster=true | `Universe/Kerbals/` is empty (or missing) → both Warn + Refuse short-circuit. Server boots normally. (Phase 6.3 lifecycle hooks will mint per-agency subdirs when each agency registers.) |
| Pre-Stage-6 universe, PerAgencyKerbalRoster=true, override=false | Warn fires + Refuse fires → server shuts down. Operator sees clear next-step options. |
| Pre-Stage-6 universe, PerAgencyKerbalRoster=true, override=true | Warn fires (operator-visible diagnostic). Refuse short-circuits (override respected). Server boots. Legacy kerbals stay on disk; Phase 6.4 routing will skip them. |
| PerAgencyKerbalRoster=true, PerAgencyCareer=false | `PerAgencyKerbalRosterEnabled` returns false. The Warn + Refuse predicates DO still fire because they only check `PerAgencyKerbalRoster` directly (operator's intent is per-agency kerbals regardless of career-gate misconfig — better to surface the kerbal-hazard NOW than silently). Phase 6.4 handler routing would no-op anyway under combined-gate=false. |
| PerAgencyKerbalRoster=true, GameMode=Sandbox | Same — Warn + Refuse fire on disk state directly. Combined gate returns false at runtime. |
| Difficulty preset selected (SetEasy/Normal/Moderate/Hard) | All four reset both flags to false. Operator must explicitly set true post-preset to opt in. Matches the PerAgencyCareer precedent. |
| `Universe/Kerbals/` exists but is empty (folder created, no files) | `GetFilesInPath` returns no `.txt`; both helpers short-circuit. |
| `Universe/Kerbals/` contains non-`.txt` files (operator notes, etc.) | The `.Where(p => Path.GetExtension(p) == ".txt")` filter ignores them. Matches the existing `LoadExistingAgencies` pattern at line 217. |
| Override flag flipped true mid-run via SettingsHandler.LoadSettings hot-reload | The Refuse predicate runs at boot time only (from `LoadExistingAgencies`). Mid-run flag changes don't re-trigger. Matches existing behavior. |
| Phase 6.4 ships and operators rebuild from `Universe/Agencies/{guid}/Kerbals/` | Override flag becomes inert because legacy `Universe/Kerbals/` has been archived. Operator can leave override=true or revert to false; the predicates correctly short-circuit when the legacy dir is empty. |
| Concurrent boots (impossible — single-process server) | N/A; the predicate is single-threaded boot-time work. |
| Universe path with non-canonical separators on Windows | `KerbalSystem.KerbalsPath` and `FileHandler.FolderExists` already handle this. No new path-handling code. |

---

## Failure modes considered

| Mode | Mitigation |
|------|------------|
| Settings round-trip drops new fields silently | The `[XmlAnyElement]` collection on `GameplaySettingsDefinition` (BUG-039 fix) handles UNKNOWN keys. The new known keys are normal `[XmlComment] + bool { get; set; }` properties — XmlSerializer treats them like any other public property. SettingsRoundTripTest pins this. |
| Difficulty preset reflection silently flips `GameDifficulty=Custom` because new fields differ from preset defaults | `SettingsHandler.HasDifferencesAgainstGivenSetting` reflects over public PROPERTIES. The new fields are properties, so they ARE reflected. But all 4 presets set both to false (matching the property default of false), so a stored value of `false` does NOT flip to Custom. A stored value of `true` will flip to Custom — same documented behavior as `PerAgencyCareer`. Acceptable per CLAUDE.md "Settings (Server/Settings/Definition/)" caveat. |
| The new XML elements appear out-of-order in the saved file because of the `[XmlAnyElement]` collection | Same as BUG-039 trade-off — known elements serialize in property-declaration order (which I place adjacent to the existing PerAgencyCareer pair); `[XmlAnyElement]` content moves to the end. Operators reading the file see consistent ordering. |
| Pre-Stage-6 universe with `Universe/Kerbals/` populated + operator flips `PerAgencyKerbalRoster=true` + restarts | Boot refuses with clear message. Operator follows fresh-start workflow OR sets override. No data loss. |
| Mid-flight server kill between adding the new fields and the first save | `SettingsBase.Load` runs SaveSettings (BUG-039 context) to rewrite the file with new known elements + preserved `[XmlAnyElement]` blob. If killed mid-write, the file may be partially written — but `LunaXmlSerializer` doesn't currently use atomic write for settings, so this risk pre-exists. Not in Stage 6 phase 6.2 scope. |
| Operator manually deletes `Universe/Kerbals/` between `WarnAboutSharedKerbalsOnUpgrade` and `RefuseStartupIfKerbalHazardWithoutOverride` (extremely narrow race, single-threaded boot) | Refuse predicate re-checks `FolderExists` and `GetFilesInPath`. If the folder now empty, no refuse. Operator sees Warn but no Fatal. Mid-boot disk mutations are operator-initiated; documented behavior. |
| `KerbalSystem.KerbalsPath` resolves differently in ServerTest vs production | Expression-bodied property mirrors `ServerContext.UniverseDirectory`, which per-test-temp-dir setups overwrite. ServerTest cases use the temp-dir convention via existing ServerHarness conventions. |

---

## Multi-lens review plan

After implementation + tests pass, run **two parallel lenses** per `[[feedback-review-lens-framing]]` + `[[feedback-rebuild-before-claiming-green]]`:

1. **server-systems-review** — the natural domain agent. Confirm: settings round-trip; `[XmlComment]` text accurate; difficulty-preset symmetry; gate-composition order (PerAgencyEnabled AND PerAgencyKerbalRoster); boot-refusal `ServerRunning = false` shape; helper short-circuit logic; `Path.GetExtension` filter matches existing patterns.
2. **persistence-review** — confirm: no disk write in Phase 6.2 (boot-time read only); `FileHandler.FolderExists` + `GetFilesInPath` usage matches conventions; no race vs boot-time `ScenarioStoreSystem.LoadExistingScenarios` ordering.

Expect 0 MUST-FIX (small surface, established precedent across 12+ existing helpers + 2 existing gate predicates). Any SHOULD-FIX gets folded into the same commit before review-receipt.

---

## Test surface delta

| Suite | Pre | Post | Delta |
|-------|-----|------|-------|
| ServerTest | 671 | ~678 | +7 (settings round-trip + 3 gate-composition + 2 boot-refusal + 1 warn-gate-off) |
| LmpClientTest | 165 | 165 | 0 (no client change) |
| MockClientTest | ~100 | ~100 | 0 (no wire change) |
| LmpCommonTest | 14 | 14 | 0 |

---

## Commit metadata

- **Branch**: `feature/per-agency`
- **Commit subject**: `feat(server,agency): Stage 6 Phase 6.2 — per-agency kerbal roster gate scaffolding`
- **Scope token**: `server,agency` (per CLAUDE.md allowed scopes)
- **No AI attribution** (silent partner rule)
- **Review receipt**: `.claude/review-receipts/{sha1}.txt` required by `require-bug-review.sh` PreToolUse hook
