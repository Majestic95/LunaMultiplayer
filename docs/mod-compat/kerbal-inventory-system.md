# Kerbal Inventory System (KIS) — compat layer analysis

**Identification.** [KIS](https://github.com/ihsoft/KIS) — sibling to [KAS](kerbal-attachment-system.md) — gives parts and Kerbals **inventory slots** that can hold other parts. Items can be picked up by EVA Kerbals, stored in containers, equipped, and re-attached. Originally KospY's mod; current maintenance: `ihsoft/KIS`.

The defining multiplayer surface is **item movement between vessels**, including cross-agency vessels, via EVA Kerbals.

---

## Luna Compat status

| In Luna Compat Harmony list? | In Part Sync list? | Server plugin flag? |
|------------------------------|---------------------|---------------------|
| **Yes** ([`lunacompat-inventory.md`](lunacompat-inventory.md), row "Historical LMP KIS fixes") | Likely yes — confirm | No |

KIS is covered by Luna Compat's Harmony layer. **Sibling KAS is not** ([kerbal-attachment-system.md](kerbal-attachment-system.md)).

---

## State ownership

| Where state lives | Shape |
|-------------------|-------|
| `Source/ModuleKISInventory.cs` (`: PartModule`) | One `[KSPField(isPersistant)] string invName`. OnLoad/OnSave persist a list of `ITEM` child ConfigNodes — one per occupied slot. |
| `Source/KIS_Item.cs` (per inventory entry) | OnSave writes: `partName`, `slot`, `[PersistentField]` group (`quantity`, `equipped`, `dryMass`, `dryCost`, `resourceMass`, `resourceCost`, `contentMass`, `contentCost`), **plus a complete `PART` child ConfigNode snapshot** of the stored part (recursive — includes resources and all its own modules). |
| `Source/Module KISItem*.cs` (Food, Bomb, Book, AttachTool, EvaPropellant, EvaTweaker, SoundPlayer) | Per-item behaviour PartModules; specialise pickup/use semantics. No additional save-game persistence beyond the item host. |
| `Source/ModuleKISItemAttachTool.cs`, `ModuleKISPickup.cs`, `ModuleKISPartDrag.cs`, `ModuleKISPartMount.cs` | EVA tool / pickup / mount behaviour — runtime, no scenario state. |
| `Source/KISAddon*.cs` (`KISAddonConfig`, `KISAddonCursor`, `KISAddonPickup`, `KISAddonPointer`) | `KSPAddon` singletons that wire UI + cursor + pickup pointer. Local; no save-game persistence. |

**No `ScenarioModule` anywhere in `Source/`.** No career, science, or contract surface.

---

## This fork touchpoints

- Scenario: **none.**
- Vessel: KIS inventory rides standard PartModule sync. The `ITEM` + nested `PART` snapshots travel inside the vessel proto bytes verbatim.
- Cross-vessel transfer (EVA pickup → re-attach to another vessel): KSP fires `Part.Couple` / `Part.Decouple` when the new part attaches; same path as docking. Inventory-side mutation is a PartModule field change on the source vessel.
- Custom relay: not used.

---

## Interaction with PerAgencyCareer

**No direct career interaction.** KIS does not touch `AgencyState.Funds`, `Science`, `Reputation`, `TechNodes`, `ScienceSubjects`, `Contracts`, `Strategies`, `Achievements`, `FacilityLevels`, or `PurchasedParts`.

**Indirect via vessel-ownership semantics for newly attached parts.** Two cases:

1. **EVA Kerbal pulls an item from agency-B's vessel inventory and stores it in their own (agency-A's Kerbal) inventory.** Vessel-B's inventory loses an item; agency-A's Kerbal vessel gains an item in its inventory. Both vessels' protos sync independently. No ownership ambiguity — each side has a clean source-of-truth.
2. **EVA Kerbal attaches the carried part to agency-A's craft.** KSP instantiates a fresh part on the destination craft. The new part inherits the destination craft's vessel-side state, including `lmpOwningAgency`. **However**, the inventory snapshot may have carried per-part metadata that, in theory, could include an `lmpOwningAgency` field (if the original source part was Stage 5.18b-stamped). KIS's `PartNodeUtils.PartSnapshot` calls KSP's standard `Part.OnSave`, which serialises whatever module fields are present.

The question for case (2): **does the `lmpOwningAgency` stamp on a snapshotted part follow the part-snapshot through KIS storage, or does the part lose its stamp on re-instantiation?** This needs a verification pass against this fork's part-save/load instrumentation. Not source-walked this session.

---

## Failure modes (multiplayer)

1. **Per-part `lmpOwningAgency` stamp survival through KIS PART-snapshot.** If the stamp survives (re-instantiated part carries the snapshot's stamp), then a KIS-attached part to agency-A's craft might bear agency-B's stamp on a single part inside agency-A's vessel — a stamp-mismatch hazard. If the stamp is stripped, the part takes its destination vessel's agency. **Behaviour TBD — verification pass needed.**
2. **Concurrent inventory access** — two players opening the same vessel's KIS inventory simultaneously. Gated by Luna MP vessel locks; same as concurrent vessel control.
3. **EVA-pickup race during proto sync** — Luna Compat's "Historical LMP KIS fixes" Harmony patches existed to address pickup/equip races. Defer to Luna Compat; verify on each Luna Compat release that the historical fix still applies.
4. **Resource bookkeeping on items containing resources** — a KIS-stored battery has its own resource state inside the PART snapshot. When the item is equipped or re-attached, resource state restores from the snapshot. No `AgencyState` field tracks this — and shouldn't, since resources are vessel-side, not career-side.
5. **Inventory visibility leak** — agency-A's player can open the right-click menu of agency-B's container and see what's inside. Same shape as "agency-A can see agency-B's parts" — Luna MP already permits vessel inspection. Not a new leak.

---

## Proposed layering

| Layer | Owner repo | Deliverable |
|-------|-------------|--------------|
| Core LMP fork | **conditional** | If the `lmpOwningAgency` stamp survives KIS snapshot round-trip and produces visible mismatches, add a hook in part-instantiation-from-KIS-snapshot that re-stamps the new part with the destination vessel's agency. Sketch below. |
| Luna Compat (Harmony/MM) | yes (existing) | Already shipped — "Historical LMP KIS fixes." No new request unless regression appears. |
| Luna Compat server plugin | no | KIS does not need server authority. |
| Operational | yes | Modlist policy already enforces KIS + version uniformity. |

### Implementation sketch — if KIS snapshot carries `lmpOwningAgency` and produces a per-part mismatch

1. **Hook**: a postfix on `KISAPI.PartNodeUtils.PartSnapshot` (or its caller) that strips any `lmpOwningAgency` field before storing the snapshot in the ITEM ConfigNode. Belongs in Luna Compat (sidecar Harmony) — not core LMP — because it's a KIS-format-specific intervention.
2. **Alternative hook**: a postfix on the destination-side re-attach (`KISAddonPickup` attach finalisation) that explicitly re-stamps the new part with the destination vessel's `lmpOwningAgency`. Same Luna Compat sidecar.
3. **AgencyState impact**: none. The existing per-vessel ownership stamp covers it; this is about preventing per-part stamp drift on a single inventory-mediated transfer.
4. **Projector impact**: none.
5. **Test**: agency-A Kerbal walks to agency-B vessel, picks up a fuel tank, attaches it to agency-A craft. Inspect the resulting part on both clients — confirm both see the new part stamped with agency-A.

This is a band-2 follow-up — defer until the verification pass confirms drift.

---

## Tests

1. Single-agency: player stores a part in a KIS container, retrieves it later, re-attaches. Confirm part state survives unchanged on both clients.
2. **Cross-agency EVA pickup**: agency-A Kerbal opens agency-B's vessel inventory, takes an item. Confirm vessel-B loses the item (both clients) and agency-A's Kerbal gains it.
3. **Cross-agency EVA re-attach**: agency-A Kerbal attaches the item to agency-A craft. Confirm new part is owned by agency-A on both clients — no stamp drift.
4. **Reconnect with non-empty inventory**: client disconnects, reconnects; inventory contents fully restore (this is the historical bug Luna Compat patches).
5. **Concurrent inventory mutation**: two clients try to take the same item simultaneously. Confirm Luna MP's lock system arbitrates (one succeeds, the other gets a clean rejection).

---

## Tracking

### Last validated

- **Fork commit:** `c36d6f97` (2026-05-18)
- **KIS upstream:** `ihsoft/KIS`, `master` branch, WebFetch pass on 2026-05-18.
- **Fork files re-read:** none directly — verification pass on `lmpOwningAgency` survival through KIS snapshot is the open follow-up.
- **KIS files inspected:** `Source/` file inventory, `Source/ModuleKISInventory.cs` (PartModule + ITEM-child OnLoad/OnSave shape), `Source/KIS_Item.cs` (per-item OnSave persists `partName` + slot + PersistentGroup fields + full PART child snapshot).
- **Findings this pass:**
  1. KIS is PartModule-only, no `ScenarioModule`, no career surface.
  2. Each inventory item embeds a **full `PART` ConfigNode snapshot** of the stored part — recursive — meaning the snapshot includes whatever the part's modules persisted, potentially including `lmpOwningAgency` if the source part was Stage-5.18b-stamped.
  3. The cross-agency stamp-survival behaviour is unverified. Two possible outcomes; both have specific test repros.
  4. KIS is already in Luna Compat — no new Harmony request unless a regression appears.
- **Gaps still open (product calls + verification):** all resolved 2026-05-18 — see below.

### Decisions ratified — 2026-05-18

| Question | Answer |
|----------|--------|
| Build proactively or verify first? | **Proactive in spec** — same precedent as KAS. If verification reveals no stamp drift, the re-attach re-stamp is an idempotent no-op, not a regression. |
| Hook location | **Re-attach re-stamp on the destination.** Harmony postfix on `KISAddonPickup` attach finalisation: the newly attached part inherits the destination vessel's `lmpOwningAgency`, overwriting any value carried through the KIS PART snapshot. Robust against future KIS snapshot format changes. **Belongs in Luna Compat sidecar** (KIS is already a Luna Compat covered mod). |

Implementation slice: see [implementation-spec.md](implementation-spec.md) §KIS.
