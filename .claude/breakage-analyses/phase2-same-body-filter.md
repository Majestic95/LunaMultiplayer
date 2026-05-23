# Breakage analysis — Phase 2 same-body relay filter

**Branch:** `feature/server-relay-filtering` (on Phase 1 commit `1e3776be`)
**Scope:** Server-side filtering of continuous vessel-state relays based on whether the sender's vessel is at the same celestial body as the recipient's active vessel. Composes on top of Phase 1's scene-aware filter.
**Spec:** [docs/research/11-server-side-offload-spec.md §4](../../docs/research/11-server-side-offload-spec.md).

---

## What changed (functional summary)

1. **`ClientStructure.ActiveVesselId` + `ActiveVesselBodyName`** — per-client cache of the recipient's active vessel + the body it's orbiting. `ActiveVesselId` captured from inbound Flightstate (by design the local-active-vessel message). `ActiveVesselBodyName` updated synchronously on Position when `VesselId == ActiveVesselId`.
2. **`Vessel.CurrentBodyName`** — atomic string cache populated by `VesselDataUpdater` (proto ingest path) + `WritePositionDataToFile` (Position update path), both under the existing per-vessel semaphore. Lock-free reads from the receive thread. Closes the M1 race the multi-lens review caught.
3. **`MessageQueuer.RelayMessageToFlightSceneSameBody<T>`** — composed Phase 1 + Phase 2 filter. Drops relays when EITHER scene filter rejects OR body filter rejects. Either gate independently operator-disablable; both off = byte-equivalent to `RelayMessage<T>`.
4. **`MessageQueuer.ShouldRelayToBody(string, string)`** — pure helper. Permissive on either side null/empty (compat for first-ingest window); `StringComparison.Ordinal` match otherwise.
5. **`MessageQueuer.ResolveSenderBody(IMessageData)`** — internal helper. Fast path for `VesselPositionMsgData` reads `BodyName` from the wire payload; falls back to `Vessel.CurrentBodyName` cache for other vessel-message types.
6. **`OptimizationSettings.SameBodyFilterEnabled`** — default true. Operator escape hatch (independent of Phase 1's `SceneAwareRelayEnabled`).
7. **`MainServer.Main` boot diagnostic** — `[perf:relay-body] enabled` / `DISABLED` mirrors the Phase 1 boot line pattern.
8. **9 + 2 call sites updated in `VesselMsgReader`** — all 9 continuous-state relays + Position case adds the `ActiveVesselBodyName` capture + Flightstate case adds the `ActiveVesselId` capture.
9. **`ForkBuildInfo`** entry `perf:relay-body`.
10. **`ServerTest/SameBodyFilterTest.cs`** — 12 cases (10 `ShouldRelayToBody` branches + 2 `ResolveSenderBody` Position-path cases via reflection).

## What didn't change

- No wire-format change (Phase 2 uses existing `VesselPositionMsgData.BodyName` field). No protocol bump.
- No client-side change at all (Phase 2 is pure server-side optimization on top of Phase 1's client mirror).
- Phase 1's behavior preserved verbatim — `RelayMessageToFlightScene` still exists; the composed function is a new entry point.
- 87 pre-feature ServerTest cases + 10 Phase 1 `SceneAwareRelayTest` cases all still pass.
- `Vessel.GetOrbitingBodyName()` unchanged (only `CurrentBodyName` cache added; existing readers like CommandHandler use the original method).

## Edge cases analyzed + how covered

| Edge case | Mitigation |
|---|---|
| Vessel just minted via proto, no Position yet | `Vessel.CurrentBodyName` set from `GetOrbitingBodyName()` in `RawConfigNodeInsertOrUpdate` — uses Orbit data already parsed from the proto ConfigNode (KSP populates Orbit.body on every proto) |
| Position arrives with empty BodyName (wire-format edge) | `WritePositionDataToFile` `if (!string.IsNullOrEmpty(msgData.BodyName))` skip — preserves last-known body rather than clobbering with empty |
| Recipient hasn't sent first Flightstate yet | `client.ActiveVesselId == Guid.Empty` → next Position can't match → `ActiveVesselBodyName` stays null → filter permissive (relay always) |
| Recipient's active vessel just changed bodies (SOI transition) | Server updates `ActiveVesselBodyName` on next Position. Up to 50ms staleness window — same body filter might drop one relay tick. Acceptable (better than continuing to filter against the OLD body) |
| Concurrent Position handler races same-body relay read | **M1 fix** — `CurrentBodyName` is a single string field on `Vessel`; writes under the per-vessel semaphore, reads are atomic per ECMA-335. Reader sees prior-or-new reference, never torn |
| Operator wants to revert just Phase 2, keep Phase 1 | `SameBodyFilterEnabled=false`; composed filter falls back to Phase-1-only logic (scene gate still applies) |
| Operator wants to revert both phases | Both `SceneAwareRelayEnabled=false` and `SameBodyFilterEnabled=false` → `RelayMessageToFlightSceneSameBody` short-circuits to `RelayMessage<T>` (byte-equivalent baseline) |
| Modded planet pack (RSS / OPM / GPP) with non-stock body names | Same-body filter is name-string comparison — works with any planet pack as long as both sides report the same body name string |
| SOI hierarchy not preserved | Spec §4.d documented — same-body-only is intentional. Mun-from-Kerbin-orbit IS dropped. Trade-off documented in OptimizationSettings.xml comment + spec |
| Phase-2 server with rollback to Phase-1 binary | SettingsBase.Load silently drops `SameBodyFilterEnabled` from disk on re-Save (XmlSerializer behavior); re-upgrade defaults back to true. Documented limitation — same shape as the pre-BUG-039 settings rollback hazard. Operator can re-edit the XML if needed |

## Test plan

- **ServerTest/SameBodyFilterTest.cs** — 12 cases covering: sender-null permissive, sender-empty permissive, recipient-null permissive, recipient-empty permissive, same-body true, different-bodies false, Moon-vs-parent-body false (regression-fence for same-body-only design), case-sensitivity (Kerbin ≠ kerbin), modded planet pack bodies (Sarnus / Earth), null-vs-empty equivalence, ResolveSenderBody on Position with body, ResolveSenderBody on Position with empty body.
- **Full ServerTest** — 109/109 passing (was 97 after Phase 1; +12 SameBodyFilterTest).
- **LmpClient build** — clean (Phase 2 is server-only; no client changes — verifies as no-op).
- **Server build** — clean.

## Multi-lens review summary

Three lenses combined (Phase 2's smaller blast radius than Phase 1 didn't warrant 3 parallel agents). Findings:

- **[MUST FIX] M1 — race on Vessel.Orbit between MessageQueuer.ResolveSenderBody and VesselPositionDataUpdater.WritePositionDataToFile.** Fix: introduced `Vessel.CurrentBodyName` atomic-string cache; reads lock-free, writes under the existing per-vessel semaphore. Same shape as BUG-033's race-on-ConfigNode-traversal fix.
- **[SHOULD FIX] S1** — Position case ordering comment (where ActiveVesselBodyName capture happens before relay). **Fixed** — expanded inline comment explaining the read/write is for the NEXT inbound, not this message's relay.
- **[SHOULD FIX] S2** — rollback to Phase-1 binary silent-drops the new XML setting. **Documented** in this analysis; doesn't block ship (operator-visible via XML re-save on re-upgrade).
- **[SHOULD FIX] S3** — `[perf:relay-scene]` and `[perf:relay-body]` both contain "enabled". Not blocking (distinct prefixes; grep on the prefix works).
- **[CONSIDER] C1** — per-relay cost analysis. Noted; Phase 3 cadence work needs to layer cleanly on top.
- **[CONSIDER] C2** — first-proto-race fallback is permissive. Verified correct.
- **[CONSIDER] C3 (test gap on ResolveSenderBody)** — **partially addressed** — 2 cases added for the load-bearing Position path. Non-VesselBaseMsgData defensive guard intentionally not tested (won't trigger from production call sites; testing would require reflection construction of internal-ctor messages with no production value).

## Known limitations + deferred items

- **Same-body-only filter (no SOI awareness).** A vessel at Mun is filtered when recipient is at Kerbin even though Mun is in Kerbin's SOI. Operator can disable via `SameBodyFilterEnabled=false`. Future improvement: optional SOI-aware mode if a cohort operator requests it (would need a server-side SOI graph — brittle against modded planet packs, deferred).
- **Stale `Vessel.CurrentBodyName` cache during proto-race window.** Brand-new vessel proto: `CurrentBodyName` set from parsed `Orbit.body`. If a Position arrives before the proto's lock + AddOrUpdate completes, `TryGetValue` misses and ResolveSenderBody returns null → permissive. Self-corrects on next Position.
- **MockClientTest e2e** — deferred to soak window (same posture as Phase 1).

## Risk classification

- **Blast radius:** server-only; affects which clients receive vessel updates, no game-state mutation.
- **Reversibility:** instant via `SameBodyFilterEnabled=false` (no restart required).
- **Wire compat:** preserved bidirectionally (no wire-format change at all).
- **Test coverage:** decision math (ShouldRelayToBody) 100% branch-covered; ResolveSenderBody Position path covered.
- **Soak guidance:** watch for "I'm at Kerbin and someone at Mun isn't visible in my tracking station" (would indicate the body filter is correctly active — known limitation per same-body-only design); the actual regression to watch for is "I'm at Kerbin and someone ALSO at Kerbin isn't visible" (would indicate a body resolution bug — neither the sender's nor recipient's body matched on a same-body case).
