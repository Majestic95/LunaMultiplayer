# Kerbal Attachment System (KAS) — compat layer analysis

**Identification.** [KAS](https://github.com/ihsoft/KAS) ships winches, struts, pipes, tow bars, and cable joints — parts that **link two arbitrary KSP `Part` instances**, optionally **coupling two `Vessel` instances into one** when the link is rigid enough. Originally KospY's mod; current maintenance: `ihsoft/KAS`.

The defining multiplayer surface is the **cross-vessel coupling** — two distinct vessels can become one (or one can split into two) at runtime.

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| **No** (not listed as of [`lunacompat-inventory.md`](lunacompat-inventory.md)) | Unknown — confirm against upstream | No |

Critically: KAS is **not** in Luna Compat's Harmony coverage. Its sibling [KIS](kerbal-inventory-system.md) is listed for "Historical LMP KIS fixes," but KAS itself has no upstream patch. This is a real gap.

---

## State ownership

| Where state lives | Shape |
|-------------------|-------|
| `Source/modules/AbstractLinkPeer.cs` (abstract base, `: AbstractPartModule : PartModule`) | Three `[KSPField(isPersistant = true)]`: `LinkState persistedLinkState`, **`uint persistedLinkPartId`** (the other peer's KSP-global `part.flightID`), `string persistedLinkNodeName` (attach node on the other peer). |
| `Source/modules/KASLinkSourceBase.cs` (extends `AbstractLinkPeer`) | Adds non-persistent `[KSPField]` config: `linkRendererName`, `jointName`, `sndPathDock`, `sndPathUndock`, `coupleMode`. Configuration, not state. |
| `Source/modules/KASLinkTargetBase.cs`, `KASLinkTargetKerbal.cs`, `KASLinkSourceInteractive.cs`, `KASLinkSourcePhysical.cs` | Subclasses of base; inherit the three persistent fields. |
| `Source/modules/KASJoint*.cs`, `KASLinkWinch.cs`, `KASLinkResourceConnector.cs` | Joint behaviour and resource-flow runtime — no save-game-level persistence beyond what the peer base records. |
| `Source/modules/KASRenderer*.cs` | Rendering helpers; pure visual. |
| `Source/controllers/ControllerWinchRemote.cs`, `ControllerPartEditorTool.cs` | Singleton handlers per repo README — local-only UI; no `ScenarioModule`. |

**No `ScenarioModule` anywhere in `Source/`.** Confirmed against the module/controller inventories. **No career, science, or contract surface.**

---

## This fork touchpoints

- Scenario: **none.**
- Vessel: KAS PartModules ride standard proto sync. The four persistent fields above carry over automatically; KSP rebinds the connection on the receiving end via `KASAPI.LinkUtils.FindLinkPeer` (which looks up `persistedLinkPartId` in `FlightGlobals.PersistentLoaded`/loaded parts).
- **Cross-vessel coupling**: `KASLinkSourceBase` with `coupleMode = Couple` invokes stock KSP `Part.Couple` / `Part.Decouple`. KSP fires `GameEvents.onPartCouple` / `onPartUndock`, which Luna MP already listens for in its docking pathway.
- Custom relay: not used.

---

## Interaction with PerAgencyCareer

**No direct career interaction.** KAS does not touch `AgencyState.Funds`, `Science`, `Reputation`, `TechNodes`, `ScienceSubjects`, `Contracts`, `Strategies`, `Achievements`, `FacilityLevels`, or `PurchasedParts`.

**Indirect interaction via `lmpOwningAgency` on coupled vessels.** This is the load-bearing concern:

- Two vessels, one owned by agency A and one owned by agency B. A player on agency A uses a KAS pipe-coupler to dock the two.
- KSP merges the vessels into one (the "kept" vessel, usually the larger/older per `Part.Couple` rules). The non-kept vessel is destroyed.
- The surviving vessel needs a single, consistent `lmpOwningAgency`. Whichever agency owned the surviving (kept) vessel wins by default — but if agency B's vessel was the kept one, the player on agency A who initiated the coupling now sees the merged vessel owned by agency B.

This is **identical in shape to the stock docking ownership problem**. If Luna MP's stock-docking code paths handle agency reassignment correctly post-merge, KAS coupling rides that mechanism for free. If they don't, KAS exposes the same bug.

---

## Failure modes (multiplayer)

1. **`persistedLinkPartId` cross-relay stability.** KSP's `Part.flightID` is per-part globally unique; Luna MP's vessel proto sync preserves it. **Confirmed not broken** in this fork against any test history, but worth a regression test when bumping LMP's proto format.
2. **Cross-agency coupling ownership reassignment.** See above — defer to docking semantics. Suggested implementation sketch in the next section.
3. **Resource transfer over KAS pipes spanning agency boundaries.** Resources flow at runtime through KAS's `KASLinkResourceConnector`. If the vessels remain separate (link mode = Link, not Couple), each side's resource state lives in that side's vessel proto. Luna MP's update-frequency cap may cause visual desync on resource bars; not a correctness issue.
4. **Link break events during proto sync** — KAS uses `KASInternalBrokenJointListener` for snap/break events. These fire client-side and propagate via standard part-state changes; should be wire-correct.
5. **KAS pipe rendering at proto load** — if `persistedLinkPartId` resolves on one client but the link rebind happens before the target vessel is loaded, the renderer can briefly show a stub. Cosmetic; resolves on next physics tick.

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | **partial** | Verify that whatever docking-merge ownership reconciliation exists for stock dock applies to KAS-coupling events. Likely no new code if stock dock works; otherwise factor the reassignment into a shared `OnPartCouple` handler that takes `lmpOwningAgency` from the surviving vessel side. |
| Luna Compat (Harmony/MM) | **yes — new** | KAS is not currently in Luna Compat's coverage. A Harmony patch sitting on `KASAPI.LinkUtils.FindLinkPeer` (post-load rebind) would let the patch validate that the rediscovered peer's vessel is one the local client knows about — defensive against proto sync ordering where the target peer arrives slightly after the source. Optional. |
| Luna Compat server plugin | no | No deterministic server authority owed for KAS — no RNG, no scenario state. |
| Operational | yes | Modlist policy already enforces KAS + version uniformity. |

### Implementation sketch — if cross-agency coupling ownership needs explicit handling

If field-test shows cross-agency KAS coupling produces inconsistent `lmpOwningAgency` after merge:

1. **Add hook**: `Server/System/Agency/AgencyVesselCoupleReconciler.cs` (new file). Listens on the existing docking-event ingress in `Server/Message/VesselMsgReader.cs` (whatever already handles `VesselProtoMsgData` after a couple event).
2. **Reconciliation rule**: when a couple event collapses two vessels into one, the surviving vessel's `lmpOwningAgency` is taken from the kept side (KSP-determined). The non-kept vessel's agency entry is cleared.
3. **AgencyState impact**: no new field. The existing per-vessel ownership stamp (Stage 5.18b `lmpOwningAgency`) covers it.
4. **Projector impact**: none — KAS produces no scenario-shape changes.
5. **Test**: two-client repro of agency-A pipe-couples to agency-B vessel; verify post-merge ownership matches the kept-side rule consistently across clients.

This is a band-2 follow-up — defer until repro confirms the bug.

---

## Tests

1. Single-agency: player couples two of their own vessels via KAS pipe. Confirm merged vessel ownership = same agency on both clients.
2. **Cross-agency couple**: player on agency A uses KAS pipe to couple to agency B's vessel. Confirm post-merge `lmpOwningAgency` matches the kept-side rule on both clients (no flickering / ownership conflicts).
3. **Decouple**: split the merged vessel via KAS decoupling. Confirm new vessel acquires a coherent agency stamp (probably whoever performed the decouple, but document the observed rule).
4. Resource transfer over a non-coupling link between agency-A and agency-B vessels: confirm resource updates respect each side's owner. (If `AgencyVesselSyncPolicy` is in force, this routes through the standard vessel-update path.)
5. Reconnect after a coupled-link save: the rebind via `persistedLinkPartId` succeeds on the joining client and the KAS pipe renderer is correct.

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **KAS upstream:** `ihsoft/KAS`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** none directly; cross-walk against `Server/System/Agency/AgencyVesselSyncPolicy.cs` (5.18d slice (i) full-sync rule), and the conceptual docking-merge ownership path in `Server/Message/VesselMsgReader.cs` (not source-walked this pass — pending if cross-agency coupling needs explicit handling).
- **KAS files inspected:** `Source/` folder structure, `Source/modules/AbstractLinkPeer.cs` (three persistent fields anchoring KAS link state), `Source/modules/KASLinkSourceBase.cs` (subclass config), `Source/modules/` file inventory, `Source/controllers/` file inventory.
- **Findings this pass:**
  1. KAS has no `ScenarioModule`, no career surface.
  2. All state lives on `AbstractLinkPeer`-rooted PartModules with three `[KSPField(isPersistant)]` fields: `persistedLinkState`, `persistedLinkPartId` (other peer's KSP `flightID`), `persistedLinkNodeName`.
  3. **Cross-agency coupling is the load-bearing concern** — rides stock KSP docking semantics, ownership reassignment is a docking-shaped problem.
  4. KAS is NOT in Luna Compat's Harmony list — a real gap relative to KIS coverage.
- **Gaps still open (product calls):** all resolved 2026-05-18 — see below.

### Decisions ratified — 2026-05-18

| Question | Answer |
|----------|--------|
| Build merge-ownership reconciler proactively, or wait for verification? | **Build proactively** as `Server/System/Agency/AgencyVesselCoupleReconciler.cs`. Generalises to **stock docking + KAS coupling + any future `Part.Couple`-triggering mod**. Belongs in core LMP fork (not Luna Compat) because it's agency-state reconciliation, not part-sync. |
| Defensive Harmony on `FindLinkPeer` for proto-sync-ordering robustness? | **Defer (YAGNI).** Transient stub render resolves on next physics tick. Add only if playtest reveals persistent visual artefacts. |
| Propose KAS Harmony coverage to Luna Compat upstream? | **Keep fork-local for now.** No KAS-specific Harmony has been identified that fits Luna Compat's pattern. Revisit only if part-sync issues surface in playtest. |

Implementation slice: see [implementation-spec.md](implementation-spec.md) §Merge-ownership reconciler (covers both stock docking and KAS coupling).
