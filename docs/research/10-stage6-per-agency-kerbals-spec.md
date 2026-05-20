# Per-Agency Kerbal Roster Spec — Stage 6

**Status:** Spec — not yet implemented. Lands on `feature/per-agency` (existing branch). No sub-branch needed; phases ship as small commits on this branch.

**Owner:** Majestic95 fork. Ground-up implementation. PlagueNZ comparison at [05a-plaguenz-audit.md](05a-plaguenz-audit.md) is benchmark only — their roster-restore bugs across `v29.01-1[4-9]` are the cautionary tale (scene-reload + reconnect surface).

**Target protocol version:** 0.31.0 (unchanged). All new behaviour is server-side disk + server-side filtering; agency-id is implicit-from-sender on inbound `Kerbal*MsgData`. No wire-shape change → no bump.

**Source audit:** comprehensive feasibility audit performed 2026-05-20 covering wire surface inventory, client touch points, vessel-proto crew handling, mod-compat hazards, KSP CrewRoster API, disk-layout options, PlagueNZ comparison, and architectural-blocker check. **Verdict: no hard blockers.** Two design calls signed off by Majestic95 (Q-Render + Q-Disk below).

**Pre-implementation gap pass (2026-05-20):** before phase 6.2 code landed, a second-round audit cross-referenced this spec against the actual `LmpClient/Systems/`, `LmpClient/Harmony/`, and `Server/System/Agency/` source. Five gaps surfaced and were addressed before the code phases began. Four were spec amendments (folded into §3, §Q-CrossAgency, §Q-K1, §9 below); the fifth was a pre-existing client-side bug (external-seat boarding had no subscriber since 2018 commit `b7306514`), fixed and shipped at commit `d500238c` (`fix(client,vessel): resubscribe external-seat board/unboard handlers`) before Stage 6 phase 6.2 code began. The gap-pass methodology is itself a useful precedent for downstream Stage-N specs — read the actual code paths the spec depends on, don't trust agent summaries without spot-reads.

---

## 1. Goal

Replace LMP's universe-wide kerbal roster (`Universe/Kerbals/{name}.txt`) with per-agency rosters under `PerAgencyKerbalRoster=true`. Each agency owns its own Jebediah / Bill / Bob / Valentina plus its own recruits, EVA rescues, deaths, and respawns. Cross-agency interactions (WOLF CrewRoutes, foreign vessel rendering) are preserved.

The fork ships **both** modes from one binary. `GameplaySettings.PerAgencyKerbalRoster` toggles, independently of `PerAgencyCareer`. Shared-roster mode is the default (v1-v7 behaviour unchanged). Per-agency-kerbal mode requires `PerAgencyCareer=true` as a precondition — the kerbal partition leans on `AgencySystem.PerAgencyEnabled` for sender→agency resolution.

This stage makes the K1 grief guard ([Server/System/KerbalSystem.cs:72-145](../../Server/System/KerbalSystem.cs#L72-L145)) obsolete under gate=on — per-agency request filtering means no one ever sees another agency's kerbals to grief them. K1 stays in place under gate=off.

---

## 2. Architecture decisions

### Q-Render — foreign-agency vessel crew rendering policy (SIGNED OFF)

**Choice:** **Scrub-foreign.** When Agency A's vessel relays to Agency B's client, B's local `HighLogic.CurrentGame.CrewRoster` does not contain A's kerbals. The existing `ScrubInvalidProtoCrew` patch family ([VesselLoader.cs:429-497](../../LmpClient/VesselUtilities/VesselLoader.cs#L429-L497), [Part_RegisterCrew.cs](../../LmpClient/Harmony/Part_RegisterCrew.cs), [KnowledgeBase_GetVesselCrewByAvailablePart.cs](../../LmpClient/Harmony/KnowledgeBase_GetVesselCrewByAvailablePart.cs)) drops unresolved names from `protoModuleCrew`. The foreign vessel renders crewless in B's tracking station.

**Cost:** zero new code. **Tradeoff:** B sees no per-seat detail on A's vessels — only the vessel's name + agency label. Acceptable cosmetic loss for v1.

**Why not stub synthesis (Option 2):** wire-message extension + name-collision disambiguation + roster-pollution GC = wide blast radius. Defer to v2 if cohort demands per-seat crew detail on foreign vessels.

**Why not server-side per-recipient byte stripping (Option 3):** vessel-proto relay is hot-path. Decompress + string-mutate + recompress per recipient is unjustified for a cosmetic gap.

**UX annotation:** the tracking-station vessel-info pane will gain a `Crew: N (agency B)` label for foreign-agency vessels in phase 6.6, summarising crew count without per-seat detail. Owner-agency vessels render unchanged.

### Q-Disk — per-agency kerbal disk layout (SIGNED OFF)

**Choice:** **Subdir per agency** — `Universe/Agencies/{agencyId}/Kerbals/{name}.txt`. One file per kerbal, scoped to the owning agency's directory.

**Why not embed in AgencyState ConfigNode (Option C):** kerbal writes are chatty (every `onKerbalLevelUp` / `onKerbalStatusChange` / `onKerbalTypeChange` + every EVA boarding event fires a `SendKerbal` from [KerbalEvents.cs](../../LmpClient/Systems/KerbalSys/KerbalEvents.cs)). Embedding kerbals in AgencyState forces every kerbal write to serialise on `AgencySystem.GetAgencyLock(agencyId)`, which would contend with Funds/Science/Tech/Contract router writes. Per-file lock domain via `FileHandler` is the right granularity.

**Why not composite filename (Option B):** ugly file paths break the operator workflow of editing a kerbal file by name.

**Why not global + lmpOwningAgency field (Option D):** the cheapest migration but it forbids same-name kerbals across agencies (one `Jebediah Kerman.txt` total). The whole point of Stage 6 is to give each agency its own Jeb.

**Path resolution helper:** new `AgencySystem.GetKerbalsPathForAgency(agencyId) → "Universe/Agencies/{guid}/Kerbals"`. Mirrors the existing `KerbalSystem.KerbalsPath` expression-bodied property. All kerbal-related FileHandler calls under gate=on route through this helper.

### Q-Seed — agency starting roster

**Choice:** each fresh-mint agency seeded with deterministic copies of the stock 4 (Jeb / Bill / Bob / Val) from `Resources.*_Kerman` templates. Same names across agencies (Agency A's Jeb and Agency B's Jeb both named "Jebediah Kerman" in their respective rosters).

**Why same names:** simplest. Each client only ever sees its own agency's `CrewRoster`, so collisions across rosters never materialise in any single `CrewRoster.Crew` list. Audit §10 Risk 1 (same-name behaviour in KSP's `CrewRoster.Crew`) is sidestepped because the question only arises if a single client holds two same-named ProtoCrewMembers, which our request-filtering design prevents.

**Implementation pattern:** new `AgencySystem.SeedStockKerbalsForAgency(state)` mirroring the existing [EnsureStartTechSeeded](../../Server/System/Agency/AgencySystem.cs) helper. Called from:
- `RegisterAgency` at fresh-mint
- `LoadAgencyFromFile` for backfill on pre-Stage-6 agency files (auto-heal mirroring the start-tech-seed pattern from `975f6208`)

Idempotent: skip seeding if any of the 4 names already exist in the agency's subdir.

### Q-K1 — K1 grief guard fate

**Choice:** keep the K1 guard in [KerbalSystem.cs:72-145](../../Server/System/KerbalSystem.cs#L72-L145) intact under gate=off (still useful for partially-rolled-out cohorts on v1-v7). Under gate=on the guard is **structurally obsolete** — request filtering means Agency B never receives Agency A's kerbal file, so B's client can't construct a `KerbalRemoveMsgData` for A's kerbal in the first place. The K1 scan runs harmlessly under gate=on but never trips a reject (the scanned vessels are all Agency-B-owned because that's all B sees).

**Sibling client-side patch — same fate.** [LmpClient/Harmony/ProtoCrewMember_Die.cs](../../LmpClient/Harmony/ProtoCrewMember_Die.cs) blocks `ProtoCrewMember.Die()` if the kerbal is locked to another player — a smaller cousin of the K1 guard from the client side. Under gate=on it's also structurally moot (Bob can't see Alice's kerbal so can't trigger their death); under gate=off it remains the only client-side defense against grief-kills on locked kerbals. Same Stage 7 cleanup target as the K1 server scan.

No removal in Stage 6 phases. Stage 7 (cleanup pass once gate=on is the default cohort) can remove both pieces of dead code.

### Q-CrossAgency — cross-agency kerbal interactions

| Interaction | Behaviour under Stage 6 |
|---|---|
| **EVA + boarding crew mutations on the local player's own vessel** | Stage 6 routes the per-kerbal-file state change (`onKerbalStatusChange` → Eva → `KerbalProtoMsgData`) per-agency. The companion `VesselProtoMsgData` (carrying the updated `protoCrew` list on the vessel) flows through the existing relay path; the v4 cross-agency write guard at [Server/Message/VesselMsgReader.cs:HandleVesselProto](../../Server/Message/VesselMsgReader.cs) already rejects cross-agency proto writes, so Bob can't broadcast crew-list changes for Alice's vessel under gate=on. Stage 6 does NOT need to extend or duplicate the v4 guard — it covers the dual-channel surface unchanged. See §3 "Wire-channel surface" below. |
| **WOLF CrewRoute cross-agency passenger handoff** | Slice E's `BuildKerbalAgencyMap` ([AgencyWolfCrewRouter.cs:372-449](../../Server/System/Agency/AgencyWolfCrewRouter.cs#L372-L449)) currently scans vessel-proto `crew = NAME` text for ownership. Under Stage 6 the kerbal is in the owning-agency's subdir; the vessel-text scan still works (the vessel proto still carries `crew = NAME`). **Re-audit Slice E** in phase 6.7 to confirm the kerbal authority gate doesn't depend on cross-agency name visibility. Initial read: it doesn't — it derives ownership from the vessel's `OwningAgencyId`, not from kerbal-file presence. |
| ~~**EVA rescue of stock-spawned asteroid kerbal**~~ ~~**Tourist contract spawning a kerbal**~~ | **MOVED TO §9 OUT OF SCOPE.** LMP currently rejects Tourism + RecoverAsset (rescue) contracts at offer time in [LmpClient/Systems/ShareContracts/ShareContractsEvents.cs:545-557](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L545-L557) — neither contract type ever enters local `ContractSystem`, so neither tourist nor rescue kerbals are spawned on any agency, ever. The original spec rows assumed these contracts would flow through; they don't. Per-agency partition is moot for both until that rejection is lifted (post-v1 work). |
| **`/setvesselagency` on a vessel carrying kerbals** | Currently a NO-OP for crew under pre-Phase-6.8 binaries. Phase 6.8 shipped (2026-05-20): under combined gate=on, the crew files migrate from `{old-agency}/Kerbals/{name}.txt` to `{new-agency}/Kerbals/{name}.txt` as part of the command via **read-then-write-then-delete (atomic per file, not atomic-across-batch)** — a mid-batch crash leaves the file in BOTH dirs and the operator re-runs (the collision pre-check then detects the dup). Reverse order (delete-then-write) would lose the kerbal on a mid-batch crash, so the write-first-then-delete order is deliberate. Same-name collision in target agency (target already has a "Jeb") REFUSES the whole command cleanly with operator-visible Error listing every collision + resolution paths. Documented at [SetVesselAgencyCommand.cs](../../Server/Command/Command/SetVesselAgencyCommand.cs). |
| **`/transferagency` (rename owner)** | NO-OP for kerbals — kerbals live under `AgencyId` subdir, not owner-name. Already handles renames cleanly because the subdir is GUID-keyed. |
| **`/deleteagency`** | `AgencyWolfMigration.CascadeOnDelete` already restores in-flight WOLF passengers to Available + ToD=0. Stage 6 extends: the agency's `Kerbals/` subdir is deleted along with the AgencyState file. Operator-visible warning lists the count of kerbals being deleted. |

### Q-Migration — pre-Stage-6 upgrade hazard

**Choice:** boot-refuse with operator opt-in flag. Mirrors the existing 6 `WarnAbout*OnUpgrade` predicates in `AgencySystem`.

New predicate: `WarnAboutSharedKerbalsOnUpgrade(out string reason)`. Trips when `PerAgencyKerbalRoster=true` AND `Universe/Kerbals/` contains files. Refuses startup unless `AllowEnablePerAgencyKerbalsOnExistingUniverse=true` in `GameplaySettings`.

**Operator workflow:** archive `Universe/Kerbals/` then flip the gate. Or: opt into the allow flag and accept that all existing kerbals become **inaccessible to in-game clients** — the legacy `Universe/Kerbals/*.txt` files remain on disk for operator inspection but no `KerbalsRequest` ever reads from that path under gate=on (the per-agency request filter at [KerbalSystem.cs:ResolveKerbalsPathForRequester](../../Server/System/KerbalSystem.cs) returns the per-agency `Agencies/{id}/Kerbals/` subdir, which Phase 6.3 mints with stock 4 only). Recruited kerbals beyond the stock 4 (hires, EVA-rescues from pre-upgrade contracts) are NOT migrated into any agency's roster. **Phase 6.4 review correction (2026-05-20):** an earlier version of this section said legacy kerbals "remain readable" post-opt-in, which was true in the literal disk-file sense but misleading about player-facing UX — players see only the stock-4 seed regardless of what's on disk. If recovering an existing recruited kerbal matters, the operator must hand-copy the relevant `.txt` from `Universe/Kerbals/` into the right `Universe/Agencies/{agencyId:N}/Kerbals/` subdir BEFORE that agency's player connects.

**Cleaner alternative not chosen:** auto-migration that attributes each `Universe/Kerbals/{name}.txt` to an agency based on vessel-of-residence at flip time. Rejected because (a) unassigned kerbals (AC pool, KIA) have no vessel-of-residence; (b) splits across multi-rostered kerbals (a kerbal serving multiple agencies' contracts simultaneously is nonsensical under per-agency, but exists in shared-mode universes). Fresh-start workflow is consistent with the existing per-agency upgrade story (`AllowEnablePerAgencyOnExistingUniverse=false` default).

**Phase 6.8 partial rescue path** (added 2026-05-20 in response to Phase 6.8 upgrade-lens review v1 Finding 1): under `AllowEnablePerAgencyKerbalsOnExistingUniverse=true`, when an operator runs `/setvesselagency V destAgency` and the moved vessel has crew aboard, the Phase 6.8 migration helper now PROBES legacy `Universe/Kerbals/{name}.txt` as a Tier-2 fallback when the per-agency source path misses. Each rescue logs an audible Warning naming both paths + the upgrade scenario. Rescued kerbal files migrate to destination's per-agency subdir AND are deleted from legacy. Counted in the new summary line token `kerbals-legacy-stranded={n}`. This is operator-initiated partial recovery, NOT a wholesale auto-migration — it only rescues kerbals aboard a vessel the operator chooses to reassign. Kerbals stranded in legacy who are NOT aboard any moved vessel stay legacy-stranded (inaccessible to clients) until the operator either hand-copies them or moves their owning vessel.

**Phase 6.7→6.8 re-run hazard** (added 2026-05-20 in response to Phase 6.8 upgrade-lens review v1 Finding 2): operators who ran `/setvesselagency V destAgency` under Phase 6.7 (no kerbal migration) and upgrade to Phase 6.8 cannot re-trigger kerbal migration via a second `/setvesselagency` because the same-stamp short-circuit at step 1 fires before the migration block runs (vessel.OwningAgencyId is already destAgency). The vessel stamp + router migration completed correctly under Phase 6.7; only the on-disk kerbal files were left behind in the SOURCE agency's subdir. **Operator recovery:** hand-copy the affected kerbal files from `Universe/Agencies/{originalSourceId:N}/Kerbals/{name}.txt` to `Universe/Agencies/{destAgencyId:N}/Kerbals/{name}.txt` BEFORE the destination's owner connects, OR `/kick` the destination owner and use `/setvesselagency V someThirdAgency` followed by `/setvesselagency V destAgency` to round-trip the vessel and trigger fresh kerbal migration (the third-agency must accept the kerbals + not have collisions). Affects only vessels previously `/setvesselagency`'d under Phase 6.7 binaries — fresh universes minted under Phase 6.8 are unaffected. No structural fix shipped in 6.8 because the detection mechanism (boot-time scan correlating vessel proto crew lists with on-disk per-agency Kerbals subdirs) would have to walk every vessel's ConfigNode text + every agency's roster on every boot, which is significant overhead for a narrow window (operators who upgraded mid-cohort AND ran /setvesselagency mid-cohort AND have crew on those vessels).

**Phase 6.8 revert hazard** (added 2026-05-20 in response to Phase 6.8 upgrade-lens review v1 Finding 3): kerbal-file moves under Phase 6.8 are persistent — rolling a binary BACK to a pre-6.8 build (Phase 6.7 or earlier) leaves moved kerbals at their post-Phase-6.8 location. The pre-6.8 build's `/setvesselagency` is NO-OP for crew, so a backwards reassignment (e.g. operator regrets the move and runs `/setvesselagency V originalAgency` under the pre-6.8 binary) will NOT reverse the file moves. Recovery: re-upgrade to Phase 6.8 and re-issue the desired `/setvesselagency` invocation — the migration logic will round-trip the files correctly. Or hand-copy under the pre-6.8 binary while the server is offline. No persisted-data corruption either way; only kerbal-file location.

---

## 3. Data model

### Server-side

**No `AgencyState` change.** Kerbals do NOT live in `AgencyState` ConfigNode — they live in the per-agency `Kerbals/` subdir (Q-Disk).

**New helper:** `Server.System.Agency.AgencySystem.GetKerbalsPathForAgency(Guid agencyId)`:
```csharp
public static string GetKerbalsPathForAgency(Guid agencyId)
    => Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N"), "Kerbals");
```

Expression-bodied so `ServerContext.UniverseDirectory` mutations (ServerTest per-test temp dirs) re-resolve correctly. Mirrors `KerbalSystem.KerbalsPath`.

**`KerbalSystem` mutations (Stage 6 scope):**

| Handler | v0 behaviour (shared) | Stage 6 behaviour (gate=on) |
|---|---|---|
| `HandleKerbalsRequest(client)` | Enumerate `Universe/Kerbals/` + send all to requester. | Resolve `client.PlayerName → agencyId`; enumerate `Universe/Agencies/{agencyId}/Kerbals/` + send only those. Fall back to legacy path if `AgencyByPlayerName` lookup fails. |
| `HandleKerbalProto(client, data)` | Write to `Universe/Kerbals/{name}.txt` via `FileHandler.WriteToFile`; relay to ALL clients. | Resolve sender's agencyId; write to `Universe/Agencies/{agencyId}/Kerbals/{name}.txt` via **`FileHandler.WriteAtomic`** (each kerbal file is the only copy of that agency's version of the kerbal — `WriteAtomic`'s `.tmp` → rename atomicity matches the AgencyState write pattern and survives mid-write server kill); relay ONLY to clients of the same agency. Gate=off path keeps `WriteToFile` (shared-roster defaults are regenerable). |
| `HandleKerbalRemove(client, msg)` | K1 gate, then delete from `Universe/Kerbals/`; relay to all. | Resolve sender's agencyId; delete from `Universe/Agencies/{agencyId}/Kerbals/{name}.txt`; relay only to same-agency clients. K1 scan is skipped under gate=on (structurally moot). |

**Lifecycle hooks:**
- `RegisterAgency(playerName)` → after creating `AgencyState`, create the `Kerbals/` subdir + call `SeedStockKerbalsForAgency(state)`.
- `LoadAgencyFromFile(path)` → after loading state, check `Kerbals/` subdir exists; if empty, call `SeedStockKerbalsForAgency(state)` (backfill for pre-Stage-6 agency files). Persist via inline `FileHandler.WriteAtomic` mirroring the start-tech-seed pattern (the SaveAgency-noop trap from `[[feedback-luna-config-node-tostring-child]]` does NOT apply here because we're writing kerbal files, not AgencyState fields).
- `TryDeleteAgency(agencyId, force)` → after WOLF cascade, recursively delete the `Kerbals/` subdir.

### Client-side

**No client-side AgencyState change.** The client receives its own agency's kerbals via the existing `KerbalReply` + `KerbalProto` relay paths. `HighLogic.CurrentGame.CrewRoster` becomes naturally per-agency-scoped because that's all the server sends.

**No new Harmony patches.** The 5 existing kerbal-related patches ([KerbalRoster_SackAvailable](../../LmpClient/Harmony/KerbalRoster_SackAvailable.cs), [KerbalEVA_*](../../LmpClient/Harmony/KerbalEVA_BoardSeat.cs), 2× [TourismContract_ClearKerbals*](../../LmpClient/Harmony/TourismContract_ClearKerbalsHard.cs)) and the 3 scrub patches all work unchanged.

**Foreign-vessel rendering:** confirmed via audit §3 — the scrub patches drop unresolved foreign-kerbal names from `protoModuleCrew` and the vessel renders crewless. No new patches needed.

### Wire-channel surface — dual-channel EVA / boarding flow

When a kerbal goes EVA or boards a part, the wire traffic splits across **two channels** and Stage 6 only needs to handle one of them:

1. **`KerbalProtoMsgData`** — fired by `KerbalEvents.StatusChange` when `onKerbalStatusChange` reports Active→Eva (and the symmetric Eva→Active on board). This is the per-kerbal-file state change that Stage 6's per-agency request filtering + `HandleKerbalProto` routing govern.
2. **`VesselProtoMsgData`** — fired by `VesselCrewEvents.OnCrewBoard` / `OnExternalSeatBoard` / `CrewEvaReady` for the seat-owner vessel + the new EVA-kerbal vessel. These carry `crew = NAME` text in the vessel ConfigNode but do NOT mutate any per-agency kerbal file.

The cross-agency safety of channel (2) is **already** handled by the v4 cross-agency write guard at [Server/Message/VesselMsgReader.cs:HandleVesselProto](../../Server/Message/VesselMsgReader.cs) (session 39, commit referenced in `ForkBuildInfo.cs` entry `v4-proto-write-guard`). Under gate=on, Bob's `SendVesselMessage` for Alice's rover is rejected before any state write. On Bob's local client the boarding still appears (cosmetic-only); the server-canonical state stays unchanged.

**Implication for Phase 6.4/6.5 implementers:** Stage 6 needs to route only channel (1) per-agency. Do NOT add a second layer of cross-agency rejection on channel (2) — the v4 guard already covers it. The audit confirmed channel (2) is already safe; channel (1) is the gap.

---

## 4. Wire protocol

**Protocol version: 0.31.0 unchanged.**

All 5 `Kerbal*MsgData` types ([LmpCommon/Message/Data/Kerbal/](../../LmpCommon/Message/Data/Kerbal/)) keep their existing wire shape. Agency-id is implicit-from-sender — the server resolves the sender's agency via `AgencySystem.AgencyByPlayerName[client.PlayerName]`. Same contract as Stage 5.16b vessel `lmpOwningAgency` stamping.

**Dual-mode silence:** under `PerAgencyKerbalRoster=false` every handler routes through the legacy shared path unchanged. Adding the gate does not change the wire shape, so a 0.31.0 client connecting to a Stage 6 server sees identical wire behaviour to a 0.31.0 client connecting to a v7 server (modulo which kerbals come down the wire).

**No new `Kerbal*MsgData` types.** The deferred `AgencyKerbalUpdateMsgData` mentioned at [05-per-agency-spec.md:116](05-per-agency-spec.md) is NOT needed — the existing `KerbalProtoMsgData` + per-agency routing suffices.

---

## 5. Settings

**New entries in [GameplaySettingsDefinition.cs](../../Server/Settings/Definition/GameplaySettingsDefinition.cs):**

```xml
<PerAgencyKerbalRoster>false</PerAgencyKerbalRoster>
<AllowEnablePerAgencyKerbalsOnExistingUniverse>false</AllowEnablePerAgencyKerbalsOnExistingUniverse>
```

**Combined gate predicate (new):**
```csharp
public static bool PerAgencyKerbalRosterEnabled =>
    PerAgencyEnabled  // existing: PerAgencyCareer && Career mode
    && GameplaySettings.SettingsStore.PerAgencyKerbalRoster;
```

Lives in `AgencySystem` alongside `PerAgencyEnabled`. KerbalSystem handlers branch on this gate.

**Caveat from CLAUDE.md "Settings (Server/Settings/Definition/)":** `SettingsHandler.HasDifferencesAgainstGivenSetting` reflects over every public property when validating against a difficulty preset. Both new settings silently flip `GameDifficulty=Custom`. Documented behaviour; either accept or fold into the deferred exclusion mechanism (5.18 deferred items).

---

## 6. Boot-refusal diagnostic

**New predicate in `AgencySystem`:**

```csharp
public static bool WarnAboutSharedKerbalsOnUpgrade(out string reason)
{
    reason = null;
    if (!GameplaySettings.SettingsStore.PerAgencyKerbalRoster) return false;
    if (GameplaySettings.SettingsStore.AllowEnablePerAgencyKerbalsOnExistingUniverse) return false;
    var legacyDir = KerbalSystem.KerbalsPath;
    if (!Directory.Exists(legacyDir)) return false;
    var legacyFiles = Directory.GetFiles(legacyDir, "*.txt");
    if (legacyFiles.Length == 0) return false;
    reason = $"PerAgencyKerbalRoster=true on a universe with {legacyFiles.Length} legacy shared kerbals at {legacyDir}. " +
             "Stage 6 requires a fresh-start workflow OR explicit AllowEnablePerAgencyKerbalsOnExistingUniverse=true. " +
             "See docs/research/10-stage6-per-agency-kerbals-spec.md §Q-Migration.";
    return true;
}
```

Called from `MainServer.Main` alongside the existing 6 `WarnAbout*OnUpgrade` checks. Refuses startup with operator-visible Fatal log.

---

## 7. Implementation phases

Each phase ships as one commit on `feature/per-agency`, with review receipt per the `.claude/hooks/require-bug-review.sh` contract.

| Phase | Scope | Tests | Effort |
|---|---|---|---|
| **6.1** | This spec doc + Q-signoff. No code. | n/a | Small |
| **6.2** | `PerAgencyKerbalRoster` + `AllowEnablePerAgencyKerbalsOnExistingUniverse` settings + `PerAgencyKerbalRosterEnabled` combined gate + `WarnAboutSharedKerbalsOnUpgrade` predicate + ForkBuildInfo entry. No behaviour change yet (gate=false default; handlers don't read the gate yet). | ServerTest: settings round-trip; boot-refusal-fires-and-passes. | Small |
| **6.3** | `AgencySystem.GetKerbalsPathForAgency` + `SeedStockKerbalsForAgency` + lifecycle hooks (mint / load-backfill / delete-cascade). Subdir created at mint, seeded with stock 4, deleted on `/deleteagency`. KerbalSystem handlers still unchanged. | ServerTest: fresh-mint creates subdir with 4 files; load-backfill heals empty subdir; delete-cascade removes subdir; round-trip stock 4 ConfigNodes. | Medium |
| **6.4** | `HandleKerbalsRequest` per-agency filter under gate=on. | MockClientTest: two-client distinct-roster e2e. ServerTest: filter respects gate; legacy path runs under gate=off. | Small |
| **6.5** | `HandleKerbalProto` + `HandleKerbalRemove` per-agency routing under gate=on. K1 guard skipped under gate=on; preserved under gate=off. Cross-agency-write rejection (sender's agency != target dir's agency — should never happen via legitimate wire path but defensive). | MockClientTest: Agency A's proto doesn't land in Agency B; Agency A's remove doesn't affect Agency B. ServerTest: relay scoped to same-agency clients. | Medium |
| **6.6** | Tracking-station UI annotation — `Crew: N (agency B)` label on foreign-agency vessels. Smoke-test that scrub patches keep foreign vessels stable steady-state under gate=on. | LmpClientTest: label formatter (pure helper following `AgencyLabelFormatter` pattern). Smoke-test in soak. | Small |
| **6.7** ✅ | **SHIPPED.** Re-audit confirmed Slice E `BuildKerbalAgencyMap` is read-only against kerbal files (reads vessel-proto bytes only). Cascade routing reworked: `CascadeOnDelete(source, destination)` overload + `CountInFlightPassengersForRefusalCheck` pre-check + new `/deleteagency --restore-to <agency>` / `--restore-to-none` flags. Operator-required disposition when gate=on + in-flight passengers exist. Per-kerbal name collision in destination DROPs with Warning (preserves destination's existing kerbal). Destination-side cascade-race guard (Phase 6.5 pattern). Pre-Phase-6.5 legacy-stranded source files fall back to `Universe/Kerbals/` probe with upgrade-hazard Warning. Cascade-after-leak post-condition Error if `TryDeleteAgency` fails after destination writes succeeded. `--restore-to` rejected under gate=off. Online destination-owner Warning recommends `/kick` to avoid concurrent KerbalProto clobber. | `DeleteAgencyCommandWolfCascadeTest` +10 (CountInFlight ×3 / gate-on happy path / `--restore-to-none` / collision-skip / partial collision / cascade-race / same-source guard / gate-off ignore / legacy fallback / both-paths-absent). `DeleteAgencyCommandParserTest` +10 (`--restore-to`/`--restore-to-none` grammar, mutex, duplicates, banner). | Small |
| **6.8** ✅ | **SHIPPED.** `/setvesselagency` crew migration. Pre-flight crew extraction via new `SetVesselAgencyCommand.ExtractCrewFromVessel` (scans the vessel's serialised ConfigNode for `crew = NAME` lines + dedupes). Lock-free collision pre-check refuses the WHOLE command cleanly (no vessel mutation, no router migration, no SaveAgency, no wire emit, no lock acquisition) when destination already has same-name kerbal files. Per-kerbal move inside the existing `RunUnderLockOrder` dual-lock between router migration (step 4) and SaveAgency (step 5): cascade-race re-check + optimistic-collision under-lock re-check (authoritative because `TryWriteKerbalProtoPerAgency` takes the same dest lock) + read-then-write-then-delete order (mid-batch crash leaves file in BOTH dirs, recoverable via re-run + collision DROP) + per-kerbal try/catch isolation. Source-path resolver handles normal source + Unassigned/orphan source legacy-fallback probe with Warning (pre-Phase-6.5 / `AllowEnablePerAgencyKerbalsOnExistingUniverse=true` upgrade-hazard path). Wire push outside the lock: each moved kerbal gets a fresh `KerbalProtoMsgData` to destination owner's client + `KerbalRemoveMsgData` to source owner's client via existing `MessageQueuer.SendToClient<KerbalSrvMsg>`. Operator-visible summary line extended with `kerbals-moved={n}` + `kerbals-dropped={n}` + per-kerbal `[fix:per-agency-kerbal-roster] moved-kerbal='...'` / `dropped-kerbal='...'` audit lines matching Phase 6.4/6.5/6.7 grammar. Gate=off keeps the historical NO-OP-for-crew posture (vessel stamp moves, kerbal files don't). Race-window posture: source-owner's in-flight `KerbalProto` after our move can write the kerbal back to source's subdir (sender = source = target tautology — operator-mitigated via `/kick` source owner before the command, same posture as the existing in-flight KolonyEntry race). | `ServerTest/SetVesselAgencyKerbalMigrationTest.cs` +10 (ExtractCrew: multi-crew / empty / vessel-not-in-store; happy A→B round-trip; empty vessel no-op; collision refuses cleanly; missing source file logged + others move; Unassigned-source legacy fallback; gate-off dual-mode silence; reverse A→B→A round-trip). | Medium |
| **6.9** | Final Frontier mod-compat audit + per-agency ribbon scenario projection (mirrors S4 DMagic pattern). **Optional** — only if cohort uses FF. | New `AgencyFinalFrontierRouter*Test` family. | Medium |
| **6.10** | Cohort soak + acceptance + merge gate. | Soak findings → backlog. | n/a |

**Total estimate:** 4-6 focused sessions for 6.1-6.8. 6.9 (FF) is +1 session and optional. Lands in the "WOLF Phase 4" effort range.

---

## 8. Acceptance criteria

For Stage 6 to merge to master:

1. Two-agency fresh-start universe: Agency A and Agency B each have their own Jeb/Bill/Bob/Val with independent level + flight-log + careerLog. Verified via in-game soak.
2. Agency A's Jeb dies on a Mun mission. Agency B's Jeb is unaffected — still Available in B's AC. Verified via soak.
3. Agency A hires a new kerbal "Wilford Kerman". Agency B's AC pool is unchanged (no Wilford). Verified via soak.
4. Agency A's vessel with Jeb aboard renders in Agency B's tracking station — vessel name + crew-count label visible; no NREs in `KSP.log` from null-crew resolution.
5. WOLF CrewRoute from Agency A → Agency B: passenger Jeb (A's) transfers to a B-owned shuttle. Slice E authority gate accepts based on vessel-ownership at Launch time. Verified via soak.
6. `/deleteagency A` deletes A's `Kerbals/` subdir along with `AgencyState.txt`. WOLF cascade restores in-flight passengers to B's AC (per Slice F) — A's Jeb does NOT migrate to B (he was A's, no longer exists). Verified via soak.
7. `/setvesselagency A→B` on a vessel carrying A's Jeb: Jeb's file migrates from `Agencies/A/Kerbals/Jebediah Kerman.txt` to `Agencies/B/Kerbals/Jebediah Kerman.txt` (per-file atomic via `FileHandler.WriteAtomic` + post-write `FileDelete`; not cross-file atomic). If B already has a "Jebediah Kerman", command refuses with operator-visible Error.
8. Pre-Stage-6 universe with populated `Universe/Kerbals/` + `PerAgencyKerbalRoster=true` + `AllowEnablePerAgencyKerbalsOnExistingUniverse=false` → server refuses to start with Fatal log pointing operator at this spec doc.
9. Same universe with `AllowEnablePerAgencyKerbalsOnExistingUniverse=true` → server starts with operator-visible Warning listing orphan kerbal count; each agency starts with seeded stock 4 in their subdir.
10. Existing test suite passes: ServerTest 670+ → 690+ (estimate +20 across phases 6.2-6.8). LmpClientTest +3 (label formatter). MockClientTest +4 e2e (two-client roster + Agency A proto/remove scoped).
11. CLAUDE.md updated: new "Stage 6" stage-roadmap section + new Stack Notes entry for the per-agency kerbal disk layout + AgencyState inventory updated.
12. ForkBuildInfo `ActiveFixes[]` extended with one Stage 6 entry per shipped phase.

---

## 9. Out of scope

- **Stub synthesis for foreign vessel crew** (Q-Render Option 2). Defer to v2 if cohort demands per-seat detail on foreign vessels.
- **Server-side per-recipient vessel-proto byte stripping** (Q-Render Option 3). Hot-path expensive; not justified.
- **Per-agency CommNet, life-support, KIS inventory state** — Kerbalism / TAC-LS / KIS are not in supported modlist and per-agency partition of their per-kerbal state is its own audit cycle.
- **Auto-migration of existing shared `Universe/Kerbals/`** — fresh-start workflow is consistent with Stage 5 upgrade hazard precedent.
- **Per-agency MissingCrewsRespawn** — the respawn timer is a `GameplaySettings` knob; under Stage 6 it ticks globally (each agency's Missing kerbals respawn at the same wall-clock cadence). Per-agency timers would require restructuring KSP's respawn machinery; not worth it for v1.
- **K1 grief guard removal** — kept in place under gate=off; removed in a Stage 7 cleanup pass once gate=on is the default cohort. Same fate for the client-side `ProtoCrewMember_Die` Harmony patch per §Q-K1 above.
- **PlayerName-based agency rename across kerbal files** — kerbals live under `AgencyId` subdir, not owner-name, so renames are NO-OP. Documented behaviour.
- **Tourism + Rescue (RecoverAsset) contract per-agency partition** — moved here from the original §Q-CrossAgency table. LMP currently rejects both contract types at offer time in [LmpClient/Systems/ShareContracts/ShareContractsEvents.cs:545-557](../../LmpClient/Systems/ShareContracts/ShareContractsEvents.cs#L545-L557); neither contract type ever enters local `ContractSystem`, so no tourists or stranded kerbals are spawned on any agency. The pre-existing TourismContract Harmony cleanup patches ([TourismContract_ClearKerbalsHard.cs](../../LmpClient/Harmony/TourismContract_ClearKerbalsHard.cs) + `Soft`) still ship and would fire if the rejection were lifted, but that lifting is post-v1 work — re-enabling these contracts under LMP requires designing how the spawned kerbals (tourist or rescue) get attributed to an agency at spawn time, which is its own audit. Stage 6 does NOT need to handle either case because they're unreachable under current LMP.
- **External-seat boarding sync (pre-existing 2018 bug)** — uncovered during the Stage 6 audit but **fixed as a separable commit** at `d500238c` (`fix(client,vessel): resubscribe external-seat board/unboard handlers`) before Stage 6 phase 6.2 code began. `ExternalSeatEvent.onExternalSeatBoard` / `onExternalSeatUnboard` were raised by Harmony patches but had no subscriber since 2018 commit `b7306514` (docking refactor collateral). Listed here so future maintainers see it was a separable concern, not a Stage 6 acceptance dependency. Closure ledger.

---

## 10. References

- Stage 5 spec: [05-per-agency-spec.md](05-per-agency-spec.md) — original per-agency design that deferred kerbal partition to Stage 6.
- PlagueNZ audit: [05a-plaguenz-audit.md](05a-plaguenz-audit.md) — benchmark only. Their per-agency roster shape + the `v29.01-1[4-9]` reconnect-bug cluster informs our test surface (reconnect handoff is a stress point).
- Stage 5 progress: [05a-stage5-progress.md](05a-stage5-progress.md) — Stage 5 implementation tracker; Stage 6 progress will follow the same per-step format.
- KSP career surface audit: [05b-ksp-career-surface-audit.md](05b-ksp-career-surface-audit.md) — singleton inventory.
- Completeness checklist: [05c-per-agency-completeness-checklist.md](05c-per-agency-completeness-checklist.md) — line 330 calls out "Original four Kerbals do not collide across agencies" as a Stage 6 acceptance point.
- Phase 4 Slice E (WOLF CrewRoute kerbal authority): [AgencyWolfCrewRouter.cs](../../Server/System/Agency/AgencyWolfCrewRouter.cs).
- BUG-023 scrub patches: [VesselLoader.cs:429-497](../../LmpClient/VesselUtilities/VesselLoader.cs#L429-L497).
- K1 grief guard: [KerbalSystem.cs:72-145](../../Server/System/KerbalSystem.cs#L72-L145).
