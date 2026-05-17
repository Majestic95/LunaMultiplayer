# PlagueNZ SplitProgression Audit

**Two-line summary:** PlagueNZ ships a server-only "per-player scenario projection" — one master `SplitCareer` toggle plus 11 granular `Split*` flags, with `PlayerCareerState` (JSON-on-disk) holding each player's funds/science/rep/tech/contracts/strategies/facilities/parts/kerbals and the server handing each connecting client a personalised copy of seven shared scenarios at handshake time. **There is no per-agency abstraction, no `OwningAgency` tag on vessels, no protocol bump, and almost no client-side Harmony interception** — the entire split design rides on the existing `Share*` and `ScenarioSystem.SendScenarioModules` wire surface, gated by server-side `if (IsXxxSplit)` checks.

## 1. Fork inventory

| Metric | Value |
|---|---|
| Branch | `master` (only branch beyond `origin/HEAD`) |
| Commits total / ahead of last upstream merge | 722 / ~120 |
| Centerpiece commit | `8c6c2e0` "Add SplitCareer mode for per-player career progression with disk persistence" (2026-04-17) |
| Author | PlagueOCE `<jacob@mcclure.nz>` (with AI-pair-programming attribution — `Co-Authored-By: Claude Sonnet 4.6` visible in commits) |
| Fork-version label | `v29.01-33` (CKAN identifier); display string `29.01-32 (split)` in `LmpVersioning.cs` |
| AVC / protocol version | `0.30.x` with `(0,30,0,29)` cross-compat row **kept** — vanilla 0.29.x clients can still connect |

**Top-level files touched by the original split commit:**
- `Server/System/ShareProgress/PlayerCareer{State,Store,Persistence,Restore}.cs` — 4 new files, ~460 lines total
- `Server/System/Share*System.cs` — all 11 modified to add the `if (IsXxxSplit) { store; return; }` early-out
- `Server/System/ScenarioSystem.cs` — `SendScenarioModules` now calls `PlayerCareerRestore.GetScenarioForPlayer` per scenario per client
- `Server/System/KerbalSystem.cs` — routes proto / request / remove through `PlayerCareerStore` when split
- `Server/Settings/Definition/GameplaySettingsDefinition.cs` — `SplitCareer` + 11 `Split*` bools + 11 `IsXxxSplit` computed helpers
- `LmpClient/MainSystem.cs` — 14 net new lines (SHA1 of `deviceUniqueIdentifier + KspPath` for the unique-id, so two installs on one box register as distinct players)

Everything else (~110 of 113 commits since fork) is **bug-fix churn** on top of that one architectural commit: kerbal roster glitches, contract-restore races with Contract Configurator, reload loops, kerbal XP cumulative display, CKAN repo packaging. Not architecture work.

## 2. Architecture decisions (vs your spec §2)

| Spec §2 decision | PlagueNZ choice | Where enforced |
|---|---|---|
| **Agency-player binding** | **No agency abstraction at all.** 1 player = 1 silo of per-player state, keyed by `playerName` (after migration from a hardware-hash key — `PlayerCareerPersistence.MigrateLegacyFile`). No team / multi-player-per-agency concept. | `PlayerCareerStore.GetOrCreate(uniqueIdentifier, playerName)` — `ConcurrentDictionary<string, PlayerCareerState>` keyed by player name |
| **Tech tree** | Per-player independent. Stored as `Dictionary<string, TechNodeInfo> UnlockedTech` (TechId → raw scenario `Tech` ConfigNode bytes). Replayed by deleting all existing `Tech` nodes from the shared scenario and inserting the player's set. | `PlayerCareerState.UnlockedTech`; `PlayerCareerRestore.ReplaceTechNodes` |
| **KSC facility upgrades** | Per-player as `Dictionary<string, float> FacilityLevels`. **Single shared physical KSC.** No visual swap — the per-player levels only affect what the client's `ScenarioUpgradeableFacilities` scenario thinks at connect time. The actual destruction model of buildings is handled by the existing shared `ScenarioDestructibles` scenario (untouched). | `PlayerCareerRestore.BuildFacilityLevels` |
| **Vessel visibility** | **All vessels visible to all players, no agency labels.** Vessels are explicitly shared by design — see the `GameplaySettings` XML comment: *"Vessels and time are always shared."* Vessel ownership is the existing implicit "whoever holds the Control lock" (`VesselLockSystem.GetControlLockOwner`). | Nothing changed in the vessel subsystem |
| **Funds / Science / Rep pools** | Per-player as scalars on `PlayerCareerState`. Persisted to disk on every disconnect (and periodic backup) via the `PlayerCareerPersistence` JSON round-trip. | `PlayerCareerState.{Funds, Science, Reputation}`; the matching `Share*System` early-outs |
| **Contracts** | **Hybrid model:** shared offered pool + per-player Active/Completed. Server writes only `Offered` contracts to the shared `ContractSystem.txt`; per-player state holds the full per-player `Contracts[]` array (Offered + Active + Finished). `BuildContractSystem` constructs the per-player scenario by emptying `CONTRACTS` and reinserting the player's full set; `BuildContractPreLoader` filters the shared pre-loader to remove contracts the player has already accepted/declined. | `PlayerCareerRestore.BuildContractSystem` + `BuildContractPreLoader` |
| **Kerbal roster** | Per-player as `Dictionary<string, byte[]> Kerbals` (KerbalName → raw scenario bytes). **Original 4 Kerbals (Jeb, Bill, Bob, Val) are seeded** for new players and for migrated saves with empty rosters (`SeedDefaultKerbals`). Each player gets their own copy with their own XP; the four are not shared instances. | `PlayerCareerStore.SeedDefaultKerbals`; `KerbalSystem.Handle*` early-outs |
| **CommNet** | **Shared infrastructure — not split.** Setting list in `GameplaySettingsDefinition` keeps the single `CommNetwork`/`RequireSignalForControl`/`RangeModifier` knobs. No per-player CommNet. | (no code — absence is the choice) |
| **Vessel ownership on dock / undock** | **No model.** Vessels are universally shared. The Control-lock owner pattern from upstream is unchanged. No agency-tagged vessel persistence, no funds attribution on recovery to a specific "owner." | (no code — `grep OwningAgency` returns zero matches in the entire repo) |
| **Save migration** | Two-layer: (1) `PlayerCareerPersistence.MigrateLegacyFile` migrates the old hardware-hash filename to a `playerName` filename automatically on first load; (2) `PlayerCareerStore.GetOrCreate` seeds defaults from `GameplaySettings.StartingFunds/Science/Reputation` when no file exists. There is **no migration FROM a shared-agency LMP save** — split mode just starts every player at the configured starting values. The old shared scenario files in `Universe/Scenarios/` continue to exist and act as the "template" the per-player projections are built on top of. | `PlayerCareerPersistence.MigrateLegacyFile`; `PlayerCareerStore.GetOrCreate` line 21–46 |

## 3. Data-model comparison (vs your spec §3)

**`PlayerCareerState`** (exact field list, `Server/System/ShareProgress/PlayerCareerState.cs`):

```csharp
public double Funds;
public float  Science;
public float  Reputation;
Dictionary<string, TechNodeInfo>          UnlockedTech;        // TechId -> bytes
ContractInfo[]                            Contracts;
Dictionary<string, byte[]>                Achievements;        // node-name -> bytes
Dictionary<string, StrategyInfo>          Strategies;
Dictionary<string, float>                 FacilityLevels;
Dictionary<string, HashSet<string>>       PurchasedParts;      // TechId -> partNames
Dictionary<string, int>                   ExperimentalParts;
Dictionary<string, ScienceSubjectInfo>    ScienceSubjects;
Dictionary<string, byte[]>                Kerbals;             // KerbalName -> bytes
```

| Spec §3 element | PlagueNZ equivalent |
|---|---|
| `AgencyState` data class | `PlayerCareerState` (per-player, not per-agency) — same shape conceptually but no `AgencyId`, no `MemberPlayers[]`, no `AgencyName` |
| `lmpOwningAgency` vessel field | **Does not exist.** Nothing on the vessel side is tagged with a per-player or per-agency identifier |
| Persistence format | **System.Text.Json** — `Universe/PlayerCareers/{playerName}.json`, written via atomic `.tmp + move` with a single-generation `.bak` rotation. No ConfigNode at the outer layer; the inner blobs (Tech / Contracts / Strategies / Kerbals / Achievements / ScienceSubjects) are **raw KSP ConfigNode bytes** stored as `byte[]` inside the JSON wrapper. That hybrid keeps the JSON readable for the top-level scalars while round-tripping the complex KSP-internal structures byte-for-byte |
| Persistence trigger | Per-player save on every disconnect (`PlayerCareerStore.SaveOne`); bulk save on periodic backup tick + shutdown (`SaveAll`); also "persist immediately on all infrequent changes" per commit `ed3d644` |

## 4. Wire protocol

| Question | Answer |
|---|---|
| Protocol version bump? | **No.** `LmpVersioning.MajorVersion/MinorVersion` follow upstream (0.30.x), and the `CrossCompatibleVersionLines` table still lists `(0,30, 0,29)`, so vanilla 0.29.x clients can still handshake against a SplitProgression server |
| New message types in `LmpCommon/Message/`? | **None.** Files in `LmpCommon/Message/Data/ShareProgress/` (`ShareProgressFundsMsgData`, `…TechnologyMsgData`, etc.) are unchanged from upstream. Audit by `git log -- LmpCommon/Message/` shows zero PlagueNZ commits there |
| Modifications to existing `Share*MsgData` to carry agency identity? | **None.** The wire payload is unchanged. Per-player attribution is recovered server-side from the `ClientStructure client` parameter on the message handler — i.e. `client.PlayerName` is the de-facto routing key |
| One wire-adjacent change | Commit `30656ff` (`SplitContracts`) added the `SplitContracts` bool to `SetingsReplyMsgData` so the client knows the server is in split-contracts mode and filters relayed updates locally. That is the *only* settings-handshake field added by the fork |

## 5. Harmony patch surface

**Total: 82 patch files in `LmpClient/Harmony/`.** That count is dominated by upstream patches that long pre-date the fork (`Funding_*`, `ContractSystem_*`, `ResearchAndDevelopment_*` event interception, `OrbitDriver_*`, etc.). The headline finding:

> **PlagueNZ does NOT add singleton-getter patches on `Funding.Instance`, `ResearchAndDevelopment.Instance`, or `Reputation.Instance` to swap state per-player.** `grep -ln "Funding.Instance"` in `LmpClient/Harmony/` returns zero matches across the entire fork's lifetime. Their architecture is **server-projects-scenario-on-connect** — singletons stay shared per-client because each client only ever sees its own scenario blob.

The fork DOES add a handful of contract / kerbal Harmony patches, but they are aimed at robustness bugs (CC popup suppression, contract Active-state restore against `KSPCF GenerateContracts`, cumulative kerbal XP display) rather than at per-player state interception. Notable additions:

- `ContractSystem_OnAwake.cs` (36 lines), `ContractSystem_OnLoad.cs` (120 lines), `ContractSystem_LoadContract.cs` (42 lines) — make ContractSystem tolerate per-player scenario layouts that vanilla upstream didn't anticipate
- `ContractPreLoader_Filter.cs` (223 lines, originally upstream, heavily edited) — filters non-CC-managed entries when injecting the personalised ContractPreLoader payload
- `KerbalRoster_CalculateExperience.cs` + `KerbalRoster_GenerateExperienceLog.cs` — implement cumulative-XP stacking (every unique `(body, achievement)` pair stacks, vs. vanilla which caps at one body)

So the surface category is: **zero patches at the per-player-state-interception layer; small surface (~5 files, ~500 LoC) at the per-player-scenario-tolerance layer.**

## 6. Genuinely surprising / unexpected design choices

1. **Server-side scenario projection avoids the singleton-interception cost entirely.** This is the elegant insight of the fork: if the server can hand each client a different `ScenarioModules` blob at connect time and gate every `Share*` relay on `if (IsXxxSplit)`, then the client never needs to know there are multiple agencies — `Funding.Instance.Funds` just *is* this player's funds because that's what got loaded. **You should explicitly consider whether your spec needs ANY client-side singleton patching** or whether you can ride the same scenario-projection trick. Per spec §2 the design appears to assume Harmony interception; PlagueNZ proves a viable alternative for the shared-physics / split-progression product slice.

2. **JSON-outer / raw-ConfigNode-bytes-inner persistence.** Top-level fields are typed JSON (`Funds`, `Reputation`); complex KSP structures (Tech nodes, contracts, science subjects, kerbals) are stored as `byte[]` blobs *inside* the JSON. This sidesteps the "do I write a full TechNodeInfo serializer?" problem entirely — they let KSP serialise to ConfigNode on the client, ship the bytes over the wire, and persist the bytes unchanged. Operationally readable for the cheap fields, opaque for the expensive ones, but provably round-trip-safe.

3. **Migration from hardware-hash → playerName key is automatic and silent.** The original v29.01-01 keyed `PlayerCareerState` by `SystemInfo.deviceUniqueIdentifier`. Later they realised this broke save portability across machines (commit `dbd0a49`). `MigrateLegacyFile` runs on every `GetOrCreate` and renames the old file in place. Lesson: **whatever key you pick first, expect to migrate it.** Your spec §3 should pick a robust identity dimension up front (player name AND/OR a server-issued opaque AgencyId).

4. **The "shared offered pool + per-player Active/Completed" contract hybrid** is a non-obvious second-iteration choice (commit `30656ff` ships it AFTER the original full-isolation v29.01-01). Without it, two players generating contracts against the same `CONTRACTS` shared file produce a chaotic merge. **Your contracts decision in spec §2 should explicitly call out which strategy: full-isolation (their v1, ugly), shared-offered-pool (their v2, elegant), or per-agency-contract-generation-with-coordination-locks (more work).**

5. **They explicitly chose NOT to add a `Group`/`Agency` abstraction.** The `Group` system upstream (your CLAUDE.md notes it's "scaffolding only") would be the natural place to hang per-agency state. PlagueNZ skipped it and went straight to `Dictionary<playerName, State>`. For YOUR spec, where per-agency teams may matter (multiple players per agency), this is a place PlagueNZ is structurally less than you need.

6. **Vessels are explicitly out of scope, hard line.** The XML doc comment on `SplitCareer` literally says *"Vessels and time are always shared."* You won't find a `lmpOwningAgency` field because they never tried. If your spec §2 has "vessel ownership routes recovery funds to the owning agency," **that is a feature beyond what PlagueNZ ships** and you cannot benchmark against them for it.

7. **AI-pair-programming attribution is right there in commit trailers** (`Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>` on `30656ff` and others), which violates your fork's "silent partner" rule. Cosmetic, but a reminder that the upstream community sensitivity isn't universal.

## 7. Open issues

`gh issue list` shows **1 open** + 4 closed (your "5 open" was probably the total-ever count). The full list:

| # | State | Title | Summary |
|---|---|---|---|
| 5 | **OPEN** | Server transfer | Single-line body: *"Universe folder attached."* Looks like an admin handoff thread, not a bug |
| 4 | Closed | KSCSwitcher site resets on client connect — scenario sync overwrites server state | Fixed by `0f78195` — `LastKSC` scenario was overwriting server's KSCSwitcher site |
| 3 | Closed | KK Launch Sites - Open All Pads Fix | Kerbal Konstructs interop |
| 2 | Closed | SplitContracts: accepted contracts lost between sessions — ContractPreLoader not personalized per-player | Fixed by `a117672` (ContractPreLoader per-player) — the bug that drove `BuildContractPreLoader` |
| 1 | Closed | Kerbals lost on reconnect — roster empty after scene reload | Fixed by the many `v29.01-1[4-9]` kerbal-related commits; tracked in `DEVLOG.md` session 2026-04-28 |

The pattern: **all four bugs are integration bugs between split-mode projection and the existing per-player-on-connect sync surface** (scenario reload, kerbal request, contract pre-load, KSCSwitcher state). Two were specifically about Contract Configurator interop — third-party mods that touch scenarios are a recurring breakage source.

## 8. Conclusion — blind spots in your spec §2 / §3

**Three places where your default decision may be naive vs. PlagueNZ:**

1. **Server-side scenario projection vs. client-side singleton patching.** Your spec §2 implies a Harmony-heavy approach. PlagueNZ proves the server-side projection alternative works, AT LEAST for the "shared-physics + split-progression" subset. **Before writing any Harmony code, decide explicitly whether you need it.** If your per-agency design adds vessel-tagged funds attribution, agency-locked craft, or per-agency facility appearance, then yes you need patches — but for the funds/science/rep/tech/contracts split alone, projection is enough.

2. **Contract architecture.** The "shared offered pool + per-player Active/Completed" hybrid is non-obvious. PlagueNZ shipped v1 with full-isolation, hit chaos with Contract Configurator and multi-player offer races, then iterated to v2. **Pre-commit to the hybrid in your spec rather than discovering it the painful way.** The Contract Configurator interop pain (Issue #2, the entire 2026-04-29 DEVLOG session) is going to be your pain too.

3. **Persistence identity key.** PlagueNZ already had to migrate from hardware-hash to player-name. For an **agency** model where multiple players can be in one agency, you need a stable `AgencyId` that survives renames and is server-issued (not client-derived). Spec §3 should pick this up front; a `MigrateLegacyFile` afterwards is fork-grade, not Stage-5-grade.

**One place where your spec is likely AHEAD of PlagueNZ and you should NOT regress to match them:**

- **Per-agency vessel ownership** (the `lmpOwningAgency` field). PlagueNZ doesn't have this — they explicitly punted with "Vessels are always shared." Don't read their absence as evidence that you don't need it; read it as evidence that they didn't ship the thing you're trying to ship. Your spec §2 vessel-ownership decision is a clean addition, not a redundancy.

**One operational pattern worth stealing wholesale:**

- The **`{playerName}.json` per-player file with `.tmp` + atomic move + `.bak` rotation** in `Universe/PlayerCareers/` is a solid persistence pattern that survives mid-write crashes. Your spec §3 doesn't yet specify a per-agency persistence layout; this is a battle-tested template — `Universe/Agencies/{agencyId}.json` mirrors it for free.
