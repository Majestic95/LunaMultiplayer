# Post-incident systemic improvements

**Triggered by:** 2026-05-20 v6 soak session — three distinct bugs in one debugging cycle:

1. **"Banned Parts" (Engineer7500)** — hand-curated default `AllowedParts` list in [LmpCommon/ModFile/Structure/ModControlStructure.cs:37-589](../../LmpCommon/ModFile/Structure/ModControlStructure.cs#L37-L589) has drifted from KSP's actual stock part set across KSP 1.10+ updates. Every stock part added since the list was last refreshed silently triggers the banned-parts dialog.
2. **Fix landed in the wrong server** — two `Server.exe` installs on the same machine (`F:/luna-multiplayer-server-runtime/` vs `C:/Users/austi/Downloads/LunaMultiplayer-Server-win-x64-Release/LMPServer/`) with no operator-visible identity to distinguish which one is "live."
3. **"Unavailable Experimental Parts"** — per-agency Career mode strips the `start` Tech node from outgoing scenarios because KSP auto-unlocks `start` at game-creation time without going through `ResearchAndDevelopment.UnlockTechWithParts`, so [AgencyTechRouter.TryRoute](../../Server/System/Agency/AgencyTechRouter.cs#L67) never sees it, so [AgencyState.TechNodes](../../Server/System/Agency/AgencyState.cs#L80) never gets a `start` entry. Closed by 2026-05-20 commit's `EnsureStartTechSeeded` helper.

**Architectural pattern in common (bugs 1 + 3):** *Server thinks the world looks like X, KSP thinks the world looks like Y, the gap silently breaks the client at vessel-build time with a generic KSP-side dialog that gives no telemetry back to the server, no warning during the handshake, no operator visibility.* Both are assumptions baked at implementation time that drift apart from upstream/runtime reality without any feedback loop to surface the drift.

Bug #2 is different (deployment hygiene) but amplified diagnosis cost for the other two.

---

## Near-term to-do — ranked by ROI

### 1. Fresh-mint + first-vessel smoke test (HIGHEST ROI)

**What:** Add one MockClientTest case that:
- Boots harness with `PerAgencyCareer=true` + `GameMode=Career`
- Seeds a minimal `ResearchAndDevelopment` scenario containing `start` with the stock starter parts (the existing [AgencyStartTechProjectionTest.cs](../../MockClientTest/AgencyStartTechProjectionTest.cs) fixture is the template)
- Connects a fresh `MockNetClient`, completes handshake → mints agency
- Sends a `VesselProtoMsgData` for a minimal Mk1Pod+parachute craft
- Asserts the server accepts the proto (no `VesselRemoveMsgData`, no reject)

**Why:** This single test would have caught bug #3 before v6 ever shipped. The agency surface has 95+ wire tests but none simulate the "first vessel build" loop, which is the exact failure mode that surfaced in production.

**Estimated effort:** 1 evening. The MockClientTest harness already handles vessel-proto round-trips elsewhere.

**File touch:** new `MockClientTest/AgencyFirstVesselSmokeTest.cs`.

---

### 2. Boot-time per-agency invariant audit + auto-heal

**What:** When `PerAgencyEnabled=true`, [AgencySystem.LoadExistingAgencies](../../Server/System/Agency/AgencySystem.cs#L141) walks each loaded agency and asserts a set of per-agency invariants. On violation, log Warning and auto-heal. The `start`-tech backfill from the 2026-05-20 fix is the first such invariant; generalise the surface so adding the next invariant is one line.

**Initial invariant set:**
- `Career mode AND TechNodes contains "start"` (auto-heal via existing [EnsureStartTechSeeded](../../Server/System/Agency/AgencySystem.cs#L2380))
- `AgencyId matches filename` (warning only; manual operator fix)
- `OwningPlayerName non-empty` (warning only)
- `AgencyByPlayerName index points back to this agency` (auto-heal: re-flip the index)

**Why:** Protects against the next "we forgot one path" gap from breaking universes silently. The `start`-tech gap is the canonical example — anything similar in future (auto-unlocked strategies? Auto-purchased default parts? Per-mode reputation grants?) gets a built-in safety net.

**Estimated effort:** 1 day. Largely a refactor of the existing 2026-05-20 helper into a dispatch table.

**File touch:** `Server/System/Agency/AgencyInvariantAudit.cs` (new), `Server/System/Agency/AgencySystem.cs` (call site in `LoadExistingAgencies` + remove the in-line `EnsureStartTechSeeded` call from `LoadAgencyFromFile`, since the boot audit covers it more uniformly).

---

### 3. Server-side telemetry for client-failed validation events

**What:** New wire message `ClientValidationErrorMsg` (additive, no protocol bump):
- Client fires when KSP's R&D / part / scenario validation throws on a player action — banned parts dialog, unavailable parts dialog, missing-required-tech, etc.
- Payload: error category enum + part / tech / scenario name + brief context string + UTC timestamp.
- Server logs each event at Warning level with a `[client-validation]` tag.
- Server aggregates counts in a circular buffer (re-use [LogRingBuffer](../../Server/Log/LogRingBuffer.cs) pattern) so `/log` and `/logjson` web endpoints surface a "last hour" tally.

**Why:** Today's session required a human chain (player → operator → assistant → root cause) to even *discover* the symptom of each bug. Telemetry inverts this: operator sees `[client-validation] 5 UnavailableParts events in last hour: 4× mk1pod, 1× parachuteSingle` and knows where to look before any player files a complaint.

**Estimated effort:** half-day per side (client wire path + server aggregation) = ~1 day total.

**File touch:** `LmpCommon/Message/Data/ClientReport/ClientValidationErrorMsgData.cs` (new), new server-side handler, client-side fire-points in the existing [BannedPartsResourcesWindow](../../LmpClient/Windows/BannedParts/BannedPartsResourcesWindow.cs) and KSP R&D validation hooks.

---

### 4. Stop shipping the hand-curated default `AllowedParts` list

**What:** Two options, operator's pick:

**4a (recommended for private cohort):** Change [ModControlStructure.SetDefaultAllowedParts](../../LmpCommon/ModFile/Structure/ModControlStructure.cs#L37) to either:
- Ship a freshly-regenerated list from a recent (1.12.5) KSP install — one operator runs the in-game LMP "Generate LMPModControl" UI once per LMP release; the result becomes the new default.
- OR set `<AllowedParts>*</AllowedParts>` semantics (wildcard) and require operators to *opt in* to a restrictive list. Trades grief-prevention for far fewer false positives. Cohort-appropriate for private servers; less so for public.

**4b (also worth doing):** Add a CI test `LmpCommonTest.ModControlDefaultsAreRecent` that fails if the default list lacks any of a small whitelist of known-stock-since-1.10 parts (`Engineer7500`, `kerbalEVAFuture`, etc.). Prevents future drift from going unnoticed.

**Why:** The current hand-curated list was last meaningfully updated around KSP 1.10. Everything Squad has added since silently triggers bug #1.

**Estimated effort:** ~1 hour (regen + replace + commit).

**File touch:** [LmpCommon/ModFile/Structure/ModControlStructure.cs:37-589](../../LmpCommon/ModFile/Structure/ModControlStructure.cs#L37-L589).

---

### 5. Server installation identity

**What:** Generate a `ServerInstallId` GUID on first boot, persist next to `GeneralSettings.xml`, surface it in:
- `/fork` web endpoint (already exposes [ForkInformation](../../Server/Web/Structures/ForkInformation.cs))
- The handshake reply (new field on [HandshakeReplyMsgData](../../LmpCommon/Message/Data/Handshake/HandshakeReplyMsgData.cs))
- Client UI: show "Connected to {ServerName} ({InstallId.Substring(0,8)})" on the status window
- Console title prefix at server boot

**Why:** Distinguish "the test server in `F:\luna-multiplayer-server-runtime\`" from "the cohort server in `C:\Users\austi\Downloads\...`" at a glance. Would have made today's bug #2 a 5-second diagnosis instead of a full investigation.

**Estimated effort:** half-day.

**File touch:** new field on `GeneralSettings`, `Server/Context/ServerInstallIdentity.cs` (new), wire field, client-side display.

---

### 6. Reinforce pre-spec call-graph lens for "what OTHER paths exist for this operation?"

**What:** Already in the playbook per [[feedback-research-first]] + the call-graph lens added in session 37 ([CLAUDE.md Stack Notes: Mod-compat S7](../../CLAUDE.md) — *"trace WHICH OTHER METHODS RUN BEFORE/AFTER your hook"*). Today's bug #3 would have been caught by a pre-spec question: *"What other paths can unlock a Tech node besides `UnlockTechWithParts`?"* (Answer: `Game.CrewAssignmentDialog` at game-creation triggers auto-unlock via direct `ResearchAndDevelopment.Instance.UnlockProtoTechNode`.)

**Action:** Add an explicit check to the per-agency design template (when writing pre-specs for new router/hook surfaces): *"Enumerate ALL upstream paths that mutate the target state. For each, verify our intercept hook fires OR explain why that path doesn't apply."*

**Effort:** Process change, ~zero code.

**File touch:** Add a section to the per-agency router template in [docs/research/05-per-agency-spec.md](05-per-agency-spec.md) — "Pre-implementation checklist: enumerate-all-mutation-paths."

---

## Pick order

If only one slot is available: **#1**. It's the cheapest catch-net for this whole class of regressions.

If two: add **#3**. Gives every future failure mode a feedback loop, not just the ones we anticipated. Without telemetry we're flying blind on player-side issues every time we ship a per-agency change.

If three: **#2**. Codifies "every per-agency Career agency must have a start tech" as an explicit invariant the server checks at boot — protects against the same fix accidentally getting reverted in a future refactor.

Items #4 + #5 + #6 are independent low-hanging fruit; pick them up opportunistically.

---

## Status

Workstream is **NOT scheduled** as of this writing — these are recommendations from the 2026-05-20 post-incident review. Schedule when v6 soak completes and `feature/per-agency` either ships or absorbs the next round of soak findings. Revisit this doc when picking up the next per-agency / mod-compat workstream.

---

## 2026-05-20 follow-up — banned-parts recurrence + items 4 + 4b + 3 partially shipped

Bug #1 ("Banned Parts" on beginner items) recurred the same day. Different specific parts than the v6 hit (Engineer7500), same root cause class: hand-curated `SetDefaultAllowedParts` list drift against KSP's actual stock part set. Shipped a structural fix that makes the failure mode no longer reachable on the default config:

**Code changes (this commit):**

- **Item 4a (wildcard / default-off):** `GeneralSettingsDefinition.ModControl` default flipped from `true` → `false` on both [Server/Settings/Definition/GeneralSettingsDefinition.cs](../../Server/Settings/Definition/GeneralSettingsDefinition.cs) and the AdminGui mirror [Tools/AdminGui/LunaServerGui/SettingsCatalog/Definitions/GeneralSettingsDefinition.cs](../../Tools/AdminGui/LunaServerGui/SettingsCatalog/Definitions/GeneralSettingsDefinition.cs). New `XmlComment` documents the rationale (drift) and the wildcard fallback so an operator opting back in has a path forward.
- **Wildcard semantics in the part-check itself** (defense in depth): [ModSystem.IsPartAllowed](../../LmpClient/Systems/Mod/ModSystem.cs) + `IsResourceAllowed` return true when `ModControlData.AllowedParts` is empty/null, so an operator who wants strict modlist enforcement (`ModControl=true`) but no part-name restriction can clear `<AllowedParts/>` in `LMPModControl.xml` to fall back on wildcard. `GetBannedPartsFromPartNames` + `GetBannedResourcesFromResourceNames` short-circuit empty too. [ProtoVesselExtension.HasInvalidParts](../../LmpClient/Extensions/ProtoVesselExtension.cs) rewired to use the new helpers.
- **Item 4b (CI drift sentinel):** New [LmpCommonTest/ModControlDefaultsTest.cs](../../LmpCommonTest/ModControlDefaultsTest.cs) pins a small set of stock beginner parts (mk1pod, parachuteSingle, basicFin, GooExperiment, kerbalEVA, etc.) and stock resources (LiquidFuel, MonoPropellant, …) in `SetDefaultAllowedParts` / `SetDefaultAllowedResources`. Catches accidental wholesale removal regressions (a refactor breaking the curated list for operators who DO opt in to `ModControl=true`). With the structural fix, this is a backstop not a hot path.
- **Item 3 (telemetry) — minimum-viable variant:** Wired existing `ChatSystem.PmMessageServer` into [FlightDriver_SetStartupNewVessel](../../LmpClient/Harmony/FlightDriver_SetStartupNewVessel.cs) and tagged the message with `[client-validation:banned-parts]` (banned-parts dialog) / `[client-validation:over-part-cap]` (max-vessel-parts dialog). [ProtoVesselExtension.HasInvalidParts](../../LmpClient/Extensions/ProtoVesselExtension.cs) was already piping the relay-side drop through `PmMessageServer`; the tag prefix unifies the two so a `grep '\[client-validation:' Logs/*.log` on the server surfaces every joiner's validation failure in one query. **Grep prefix availability is gated on `ModControl=true`:** under the new default `ModControl=false`, the banned-parts surface never fires so `[client-validation:banned-parts]` events never reach `/log`. The `[client-validation:over-part-cap]` events DO still fire under default-off (the max-vessel-parts check is independent of the mod-control system). The full `ClientValidationErrorMsgData` + LogRingBuffer integration from item #3 above remains queued — bigger structural surface (new wire message type, server-side handler, ring-buffer aggregation), worth a dedicated commit when there's appetite. The chat-channel variant gets us 80% of the diagnostic value for 10% of the work.

- **Server-side mirror of the wildcard fix in [VesselDataUpdater.cs:83-101](../../Server/System/Vessel/VesselDataUpdater.cs#L83-L101):** caught by the upgrade-lens review on this commit. The server-side parts-check originally used `vesselParts.Except(ModFileSystem.ModControl.AllowedParts)` which had the same `.Except(empty) = all-inputs` semantic bug as the client-side `HasInvalidParts`. Recovery path (b) advertised in the new XmlComment ("clear `<AllowedParts/>` to fall back on wildcard semantics") would have silently bricked vessel ingest because the server would reject every vessel-proto. Fix: null/empty short-circuit on `ModFileSystem.ModControl?.AllowedParts` BEFORE the `.Except` runs. Also closes a latent NRE path where an operator flipping `ModControl=true` at runtime via `/changesettings` (without restart) hits the next `HandleVesselProto` against a still-null `ModFileSystem.ModControl` (the initial `LoadModFile` is gated on `ModControl=true` at boot — see [MainServer.cs:180](../../Server/MainServer.cs#L180)).

**Items still queued:**

- **Item 1** (fresh-mint + first-vessel smoke test) — was about per-agency start-tech, not parts allowlist. Independent of this commit.
- **Item 2** (boot-time per-agency invariant audit + auto-heal) — independent of this commit.
- **Item 3 full ClientValidationErrorMsg** — deferred, see above. The chat-channel variant in this commit may make the full wire message unnecessary; revisit if the chat tag proves insufficient for aggregation.
- **Item 4a regen-from-KSP-1.12.5** — not done. The hand-curated list is left in place because the runtime wildcard + default-off makes it no longer the hot path. Operators opting in via `ModControl=true` get the curated list as a "1.10-era stock parts" baseline; they can regenerate it in-game via the existing "Generate LMPModControl" UI if they need a current set.
- **Item 5** (server installation identity) — independent of this commit; defer.
- **Item 6** (pre-spec call-graph lens) — already in playbook, no code change.

**Operator deployment steps for the recurrence:**

Either path unblocks an existing server:

1. **Config-only (fastest, no rebuild)** — edit `<server-install-dir>/Settings/GeneralSettings.xml`, change `<ModControl>true</ModControl>` to `<ModControl>false</ModControl>`, restart. No new binary needed.
2. **Code-update path** — deploy the new server binary built from this commit. Fresh installs get the new default automatically. Existing installs with a `GeneralSettings.xml` already on disk retain the prior value (XML round-trip preserves it), so even after redeploying the binary the operator still needs to either (a) edit the file as in path 1, or (b) delete the file so the server regenerates it with the new default.

The wildcard runtime fallback means even an operator who keeps `ModControl=true` can recover by clearing `<AllowedParts/>` to an empty `<AllowedParts />` in `LMPModControl.xml`.
