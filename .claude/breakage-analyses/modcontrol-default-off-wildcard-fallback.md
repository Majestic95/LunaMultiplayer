# ModControl default-off + wildcard fallback + drift sentinel + chat-tagged telemetry — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `9633025a` (Stage 6 Phase 6.10-followups close-out, v0.31.0-per-agency-private-8.1 tip)
**Discipline:** Per `[[feedback-breakage-analysis]]`.

**Motivation:** "Banned Parts" KSP dialog recurred on the cohort 2026-05-20 — same root cause class as the v6 soak (post-incident doc 09 bug #1): hand-curated `ModControlStructure.SetDefaultAllowedParts` list at `LmpCommon/ModFile/Structure/ModControlStructure.cs:37-588` has drifted against KSP's actual stock part set since the list was last refreshed around KSP 1.10. Every stock part Squad has added since (Engineer7500 confirmed via `ServerTest/XmlExampleFiles/Others/75accdfb-...txt`, plus the unnamed beginner-tier parts that triggered this session) silently triggers the banned-parts dialog on every joiner, blocking even mk1pod-class craft. The user picked the "wildcard / disable part-list check" + "CI drift sentinel" + "client→server validation telemetry" options from `AskUserQuestion`. This commit ships a minimum-viable variant of all three plus documentation.

---

## Scope lock — IS

### 1. `Server/Settings/Definition/GeneralSettingsDefinition.cs` — flip `ModControl` default `true` → `false`

The hot-path fix. Fresh server installs no longer enter the parts-allowlist failure mode at all. New `XmlComment` (8 lines) replaces the original 3-line warning and documents:
- Why the default flipped (drift against KSP stock parts; `LmpCommon.ModFile.Structure.ModControlStructure.SetDefaultAllowedParts` last refreshed ~KSP 1.10)
- The two recovery paths for operators who want strict modlist enforcement: regenerate `LMPModControl.xml` in-game OR clear `<AllowedParts/>` to fall back on the new wildcard semantics
- Pointer to the wiki for the original design

Existing servers with a `GeneralSettings.xml` already on disk retain whatever value the file has (XML round-trip preserves it on `LoadSettings`/`SaveSettings`) — the operator still needs to (a) hand-edit the file or (b) delete it so the server regenerates with the new default. The XmlComment explicitly says so.

### 2. `Tools/AdminGui/LunaServerGui/SettingsCatalog/Definitions/GeneralSettingsDefinition.cs` — symmetric flip

The AdminGui ships a byte-equivalent copy of `GeneralSettingsDefinition` per the established pattern. Both get the same `= false` default + the same XmlComment so an operator editing settings via the GUI sees identical guidance.

### 3. `LmpClient/Systems/Mod/ModSystem.cs` — wildcard semantics + helper functions

Defense-in-depth for operators who DO opt back in to `ModControl=true`. Two new public helpers:

```csharp
public bool IsPartAllowed(string partName)
{
    if (ModControlData?.AllowedParts == null || ModControlData.AllowedParts.Count == 0)
        return true;
    return ModControlData.AllowedParts.Contains(partName);
}

public bool IsResourceAllowed(string resourceName) { /* sibling */ }
```

`GetBannedPartsFromPartNames` + `GetBannedResourcesFromResourceNames` also short-circuit to `Enumerable.Empty<string>()` when the underlying list is null/empty. Same semantics as the helpers — call sites in `FlightDriver_SetStartupNewVessel.cs` already use these.

Wildcard semantics let operators with `ModControl=true` (so modlist enforcement / forbidden plugins / required expansions still fire) clear `<AllowedParts/>` in `LMPModControl.xml` to drop the parts-name restriction without losing the rest of the mod-control surface.

### 3b. `Server/System/Vessel/VesselDataUpdater.cs` — server-side mirror of wildcard fix (added post-review)

The upgrade-lens reviewer caught a [MUST FIX]: the SERVER-side parts-check at line 86 had the SAME `.Except(empty) = all-inputs` semantic bug as the client-side `HasInvalidParts`. Recovery path (b) advertised in the new XmlComment ("clear `<AllowedParts/>` to fall back on wildcard semantics") would have bricked vessel ingest at server side — `Except(emptyList)` returns all input parts, every one flagged banned, `return` before the vessel lands in `CurrentVessels`. Fixed in-place by null/empty short-circuit on `ModFileSystem.ModControl?.AllowedParts` BEFORE the `.Except` runs. The same change also closes a latent NRE path where an operator flips `ModControl=true` at runtime via `/changesettings` without restart — the initial `LoadModFile` is gated on `ModControl=true` at boot ([MainServer.cs:180](../../Server/MainServer.cs)), so `ModFileSystem.ModControl` stays null, and the next `HandleVesselProto` NREs at the prior `ModFileSystem.ModControl.AllowedParts` access.

### 4. `LmpClient/Extensions/ProtoVesselExtension.cs` — rewire to helpers

Two changes inside `HasInvalidParts`:
- Replace `!ModSystem.Singleton.AllowedParts.Contains(pps.partName)` with `!ModSystem.Singleton.IsPartAllowed(pps.partName)` — picks up wildcard.
- Replace `.Except(ModSystem.Singleton.AllowedResources)` with `.Where(r => !ModSystem.Singleton.IsResourceAllowed(r))` — picks up wildcard. The `.Except` shape was a bug under wildcard intent: `.Except(empty)` returns ALL inputs (KSP's stock resources), so the original code would have flagged everything as non-whitelisted if the list was empty. The new shape filters nothing when wildcard.

Add `[client-validation:banned-parts]` tag prefix on the existing `LunaLog.LogWarning` + `ChatSystem.Singleton.PmMessageServer` so it grep-aligns with the new FlightDriver telemetry.

### 5. `LmpClient/Harmony/FlightDriver_SetStartupNewVessel.cs` — telemetry via `PmMessageServer`

The banned-parts dialog at this site previously logged client-side only (`LunaLog.LogError`). Add a one-line `ChatSystem.Singleton.PmMessageServer(...)` call right before opening the dialog with the `[client-validation:banned-parts]` tag (or `[client-validation:over-part-cap]` for the max-vessel-parts branch). Operator's server log surfaces every joiner's validation failure under one grep prefix.

Why not the full `ClientValidationErrorMsgData` from post-incident doc item #3? That's a new wire surface (MsgData + handler + LogRingBuffer aggregation), ~1 day of work. `PmMessageServer` is the existing client→server admin-chat channel that's ALREADY used by `ProtoVesselExtension.HasInvalidParts` for the relay-side banned-parts drop. Reusing it for the FlightDriver site gets 80% of the diagnostic value (operator-visible event tally on `/log`) for 10% of the work. The full wire surface remains queued.

### 6. `LmpCommonTest/ModControlDefaultsTest.cs` (new) — drift sentinel

Two test methods:
- `SetDefaultAllowedParts_ContainsStockBeginnerAnchor` — pins 13 stock beginner parts (mk1pod / parachuteSingle / basicFin / liquidEngine / fuelTankSmall / stackDecoupler / launchClamp1 / GooExperiment / longAntenna / solarPanels1 / kerbalEVA / kerbalEVAfemale / flag) in the default list. Catches accidental wholesale removal during refactors.
- `SetDefaultAllowedResources_ContainsStockResources` — pins 7 stock resources (LiquidFuel / Oxidizer / SolidFuel / MonoPropellant / ElectricCharge / Ore / Ablator). Resources have historically been less prone to drift; this is more of a regression net than a drift catcher.

The sentinel only guards operators who explicitly opt in to `ModControl=true` (the curated list is now the cold path). XML doc explicitly says so. New `[client-validation:*]` chat-tag telemetry is the operator-visible signal when the failure mode IS reached.

### 7. `docs/research/09-post-incident-systemic-improvements.md` — append "2026-05-20 follow-up" section

New section appended after the existing Status block (does not modify the original 6-item ranked recommendations above it). Documents:
- Items shipped in this commit (4a partial, 4b, 3-minimum-variant)
- Items still queued (1, 2, 3-full-MsgData, 4a-regen, 5, 6)
- Operator deployment steps (config-only path + code-update path)

### 8. `CLAUDE.md` — new Stack Notes entry

Append a chronological entry under "Stack Notes & Patterns Learned" capturing the general pattern: *server-side enumerations of external-source-of-truth state (KSP stock part set, KSP-mod ScenarioModule shapes, auto-unlocked techs) drift on every external update; the cheap structural fix is to stop enumerating — replace allow-list with wildcard, per-mod enum with generic catch-all, curated seed list with runtime probe.* Same shape as the per-agency-start-tech-seed bug from the same day.

---

## Scope lock — IS NOT

- **NOT** regenerating `SetDefaultAllowedParts` from a 1.12.5 KSP install (post-incident item #4a-regen). The user picked the wildcard option; with default-off, the curated list is no longer the hot path. An operator opting in to `ModControl=true` can regenerate in-game via the existing "Generate LMPModControl" UI; we don't need to bake a current snapshot into the binary.
- **NOT** the full `ClientValidationErrorMsgData` wire surface (post-incident item #3 full). Deferred; the chat-channel variant covers the diagnostic need. Revisit if `[client-validation:*]` chat tags prove insufficient for aggregation (e.g. when an operator wants `/logjson` to expose the structured tally separate from chat).
- **NOT** the fresh-mint + first-vessel smoke test (post-incident item #1). Independent — about per-agency, not parts allowlist.
- **NOT** the boot-time per-agency invariant audit (post-incident item #2). Independent.
- **NOT** the server installation identity surface (post-incident item #5). Independent.
- **NOT** removing the curated list from `SetDefaultAllowedParts`. The list stays as a "1.10-era stock parts" baseline for operators who explicitly opt in. The CI sentinel protects them; the runtime wildcard is the structural fix for everyone else.
- **NOT** changing the wire protocol. Stays at 0.31.0. The XmlComment + chat-tag are additive doc / log changes; no `Lidgren` message-type enum slot consumed.
- **NOT** the AppData / Universe path migration. Tangentially related to the post-incident doc's bug #2 (wrong-server-folder deploy) but explicitly deferred — see item #5 above.

---

## Edge cases

1. **Existing servers with `GeneralSettings.xml` already on disk (ModControl=true).** XML round-trip preserves the existing value on `LoadSettings`. Operator MUST either hand-edit the file OR delete it. Documented in the XmlComment + in post-incident doc 09 "2026-05-20 follow-up" + in the operator-facing summary. Not a regression — same posture as any other settings-default change.
2. **Existing servers with `LMPModControl.xml` already on disk.** The file is parsed via `LunaXmlSerializer.ReadXmlFromPath<ModControlStructure>` and held in `ModFileSystem.ModControl`. Under `GeneralSettings.ModControl=false`, the file is loaded but its contents are never consulted (the client check is gated by `if (ModSystem.Singleton.ModControl)` which is fed by the handshake reply's `ModControl` field, which is `GeneralSettings.ModControl` server-side). So an existing `LMPModControl.xml` with stale `AllowedParts` becomes inert under default-off — desired behaviour.
3. **`AllowedParts = null` vs `AllowedParts = empty list`.** The XML serializer round-trips an empty `<AllowedParts/>` to `new List<string>()` (count == 0) and a missing `<AllowedParts>` element to whatever the property initializer says — currently `= new List<string>()`. Both shapes get the wildcard short-circuit in the new helpers (`null || Count == 0`).
4. **`ModControlData == null`.** Can happen if the client hasn't completed the handshake yet when `IsPartAllowed` is called. The new `ModControlData?.AllowedParts == null` check covers this — returns true (wildcard). Safer than the prior `.AllowedParts.Contains(...)` which would have NRE'd.
5. **Banned-resource semantics under wildcard.** `pps.resources` could contain resource names not in `PartResourceLibrary.Instance.resourceDefinitions` (modded resources the client doesn't have a definition for). Under wildcard, `IsResourceAllowed` returns true for all of them, so the `nonWhitelistedResources` filter drops them all. The downstream `if (!verboseErrors) ...` block runs zero times. Desired — the vessel loads with the missing resource, which `Validate` covers separately at the part-vs-resource-mismatch path (line 69-77 in `ProtoVesselExtension.cs`).
6. **Telemetry under offline server / disconnect.** `ChatSystem.PmMessageServer` enqueues a `ChatMsgData` for delivery. If the client is currently disconnected (which shouldn't happen at this site — `FlightDriver_SetStartupNewVessel.PrefixSetStartupNewVessel` early-exits when `NetworkState < ClientState.Connected`), the message would queue and either deliver on reconnect or be dropped at `Disconnect` — acceptable; this is best-effort telemetry, not a wire correctness gate. `HasInvalidParts` has the same `if (verboseErrors)` guard which is itself gated on `MainSystem.NetworkState < ClientState.Connected` upstream.
7. **CI sentinel currently passes — what if someone removes those 13 parts intentionally?** The XML doc says "Either (a) restore the missing entries OR (b) accept the loss and remove them from `StockBeginnerPartsAnchor` (which signals a deliberate scope reduction)." The test message is verbose so a future maintainer can make the call.
8. **`StockBeginnerPartsAnchor` is hand-curated too — does it have the same drift problem?** Yes, but at MUCH smaller scope: 13 entries vs ~550. The anchor only needs to catch *accidental wholesale removal* of the curated list (the failure mode this sentinel is for). It's not trying to be a "current KSP stock parts" snapshot. If someone refactors and accidentally truncates the list, the sentinel fails. If a future KSP update adds a 14th beginner-tier part we forgot to anchor, the sentinel still passes — but the runtime wildcard means that's not a production failure.

---

## Test surface

- `LmpCommonTest/ModControlDefaultsTest.cs` — 2 new tests covering the anchor surface. **Verified locally:** `dotnet test --filter "FullyQualifiedName~ModControlDefaultsTest"` → 2/2 pass.
- `Server.csproj`, `LmpClient.csproj`, `LmpCommon.csproj`, `LmpCommonTest.csproj` — all build clean (verified locally; only pre-existing warnings).
- **NOT** adding a MockClientTest for the wildcard path. The wildcard fires on the CLIENT (`ModSystem.IsPartAllowed`), the client side is `net472` Mono and not exercised by MockClientTest (which only runs Server-side wire tests on `net10.0`). The matching surface IS exercised by `LmpClientTest` — but `LmpClientTest` doesn't currently exercise `ModSystem` either (KSP-bound — depends on `ModSystem.Singleton` which depends on KSP runtime). Adding test coverage for the wildcard would require building a `ModControlStructureTestHarness` or similar — out of scope for this commit; revisit if the wildcard ever breaks in production.
- **NOT** adding `ServerTest` coverage for the default flip. `GeneralSettings.SettingsStore.ModControl` is already exercised by `HandshakeSystemValidatorTest` (line 35-38 in that file explicitly sets it to false for the handshake test). The default-value test would be tautological with `[XmlElement(Value = false)]` round-trip — and `LunaXmlSerializerTest` already covers XML round-trip generically.

---

## Risk

- **Wire protocol change:** none. Stays at 0.31.0.
- **Persistence schema change:** none. `GeneralSettings.xml` schema unchanged; the default value flip is XmlSerializer-transparent (existing files preserve their on-disk value).
- **Migration:** None. The operator is expected to either edit existing `GeneralSettings.xml` to set `ModControl=false`, or delete the file. The post-incident doc 09 "2026-05-20 follow-up" section documents both paths.
- **Backward compat with older clients:** None affected. Older clients connecting to a server with `ModControl=false` simply receive `ModControl=false` in the handshake reply, which they already handle (existing behavior on private servers running `ModControl=false`).
- **Rollback:** Trivial — `git revert` the commit. Existing `GeneralSettings.xml` files would now produce a stale-default warning at boot (`ModControl=false` from a regenerated file, but operator could have left their value at `true`).

---

## Independent review hooks

Per `[[feedback-independent-review]]`:
- Production `.cs` files changed: `GeneralSettingsDefinition.cs` (Server side), `GeneralSettingsDefinition.cs` (AdminGui mirror), `ModSystem.cs`, `ProtoVesselExtension.cs`, `FlightDriver_SetStartupNewVessel.cs`.
- New test file: `ModControlDefaultsTest.cs` (test path, exempt from review-receipt gate).
- Docs: `09-post-incident-systemic-improvements.md`, `CLAUDE.md` (both exempt).
- Review lenses to run on the production files:
  - **Consumer-lens:** what's the experience of an operator running an existing server when they deploy the new binary without editing config?
  - **Upgrade-lens:** what's the experience of an operator running an existing server with `LMPModControl.xml` containing the curated allowlist + `ModControl=true` in `GeneralSettings.xml`?
  - **Security-lens:** does dropping the parts allowlist by default open a grief vector on the private cohort? (Pre-existing answer: the cohort threat model has trusted players; the parts allowlist was never the load-bearing grief defense — `RejectIfCrossAgencyWrite` + `KerbalNameValidator` from Phase 6.9-hardening are.)
