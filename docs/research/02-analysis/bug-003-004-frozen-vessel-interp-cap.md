# BUG-003 / BUG-004 — Remote vessel "frozen" stall (symmetric interpolation cap)

**Implementation order:** **3rd** in the Option C sequence.

**Status:** Validated against `master` at commit `48df64bd` (2026-05-16). Diagnoses from [03-time-sync-fix-brainstorm.md](../03-time-sync-fix-brainstorm.md#bug-2--remote-vessel-appears-frozen-stall-not-drift) verified by direct code read. Smallest-possible diff; highest critic confidence.

**Inventory entries:**
- BUG-003 (#129 "Interpolation causes hard de-sync when warping or syncing subspaces") — needs verification
- BUG-004 (#251 "Ships taken back in time and teleported when an earlier-subspace player syncs forward") — closed unresolved

These two are the same underlying interpolation-asymmetry bug seen from different vantage points. One Phase-3 fix addresses both.

---

## Symptom

When a remote player is in a subspace **far in the future** of the local player, their vessel appears frozen for long periods, then snaps when a closer-bracketing position update arrives. Originally reported as "drift" — that framing is wrong; it's a stall, not unbounded drift.

## Code locations (validated)

- [LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs:68-70](../../../LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs#L68-L70) — `MaxInterpolationDuration` returns `~2x SecondaryVesselUpdatesMsInterval` for past/equal subspaces, `double.MaxValue` for future.
- [LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs:77](../../../LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs#L77) — `InterpolationDuration => LunaMath.Clamp(Target.GameTimeStamp - GameTimeStamp + ExtraInterpolationTime, 0, MaxInterpolationDuration)`.
- [LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs:81](../../../LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs#L81) — `NumFrames => (int)(InterpolationDuration / Time.fixedDeltaTime) + 1`.
- [LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs:78](../../../LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs#L78) — `LerpPercentage => Mathf.Clamp01(CurrentFrame / NumFrames)`.

## Diagnosed root cause (validated)

The cap is asymmetric:

```csharp
private double MaxInterpolationDuration => WarpSystem.Singleton.SubspaceIsEqualOrInThePast(Target.SubspaceId) ?
    TimeSpan.FromMilliseconds(SettingsSystem.ServerSettings.SecondaryVesselUpdatesMsInterval).TotalSeconds * 2
    : double.MaxValue;
```

For a future-subspace target N seconds ahead:
- `InterpolationDuration ≈ N` seconds (effectively unclamped because `MaxInterpolationDuration = double.MaxValue`).
- `Time.fixedDeltaTime ≈ 0.02s` → `NumFrames ≈ 50N` per FixedUpdate.
- Each frame advances the vessel by `1 / NumFrames` of the position delta — so motion ≈ `1 / 50N` of normal speed.

For N = 60s (one minute of subspace delta) that's ~1/3000 of normal motion. The vessel looks frozen, then snaps when a fresher update arrives.

## Critic-corrected premise (load-bearing — record so future devs don't relearn it)

An earlier (rejected) framing blamed Unix-epoch-scale numbers losing precision (cited PlagueNZ commit `17f60aa`). **That framing is wrong.** `GameTimeStamp` is KSP game UT — typically 1e3-1e6 seconds since campaign start, not Unix epoch. Precision is not the bottleneck. PlagueNZ's commit patched a different value (server `CurrentUT`) and is not relevant to this client-side path.

## Recommended fix (Option A — symmetric cap)

**One-line change.** Mirror the past-subspace cap on the future side:

```csharp
private double MaxInterpolationDuration =>
    TimeSpan.FromMilliseconds(SettingsSystem.ServerSettings.SecondaryVesselUpdatesMsInterval).TotalSeconds * 2;
```

Or, if a wider future window is desired (to smooth large but recoverable deltas), keep an asymmetry but cap the future side at a finite multiplier:

```csharp
private double MaxInterpolationDuration => WarpSystem.Singleton.SubspaceIsEqualOrInThePast(Target.SubspaceId) ?
    TimeSpan.FromMilliseconds(SettingsSystem.ServerSettings.SecondaryVesselUpdatesMsInterval).TotalSeconds * 2
    : TimeSpan.FromMilliseconds(SettingsSystem.ServerSettings.SecondaryVesselUpdatesMsInterval).TotalSeconds * 10;
```

Visible effect: vessel skips to its latest known pose rather than crawling. This is what users actually want — the stall behavior was a math accident, not a design choice.

## Out of scope / rejected alternatives

- **Configurable horizon constant `MAX_SUBSPACE_DELTA_SECONDS` + freeze + UI label** — rejected by critic: introduces a UX regression (you can't see a friend's vessel move during a rendezvous when subspaces are >30s apart), and KSP's physics-load range can't be gated on this constant. File as RFC, not in this fix.
- **Project the future-subspace position forward in time** — would help visually but requires re-solving the orbit at the local UT, expensive and brittle. Subsumed by Bug 1's solo-subspace fix landing first (which reduces the cases where subspaces drift apart for long stretches).

## Test plan

**Hard-to-test in `ServerTest`** because this is client-side and depends on `Time.fixedDeltaTime` + `FlightGlobals`. Until Stage 4 (mock-client harness) lands, the validation path is:
1. Code review + the one-liner is mechanically obvious.
2. Soak test: two clients, one in subspace A, other in subspace A+60s; observe vessel skip rather than crawl.
3. Regression check: existing past/equal-subspace behavior unchanged (cap stays at `~2x` interval).

A unit test could be carved out of `VesselPositionUpdate` if the orbital math were pure — currently it depends on `Vessel`, `FlightGlobals.Bodies`, and `Planetarium` statics, so the dependency surface to mock is too large to justify pre-Stage-4.

## Dependencies

- **None.** Smallest possible diff (one line + optional second line for asymmetric cap). Isolated.
- **Loose ordering hint:** Bug 1 lands first in Option C so that on a rendezvous where subspaces are forced apart by the solo-fix interaction, the symmetric cap is already in place.

## Risks

- **Vessels in genuinely distant futures will pop instead of slide.** That's the desired behavior; flagging in case anyone considers the existing crawl "intentional."
- **`NumFrames` downstream consumers** — search for other uses of `NumFrames` and `LerpPercentage` before shipping. As of `48df64bd`, both are only consumed inside `VesselPositionUpdate` itself.

## Open questions

- **Asymmetric cap (10x) or symmetric (2x)?** 10x is more forgiving and may smooth ~1-5s deltas without visible pop. Pick 10x first; tighten to 2x if UX testing shows the pop is undesirable.
- **Settings exposure:** add `SettingsSystem.ServerSettings.MaxFutureInterpolationMultiplier` so operators can tune? Probably yes — costs nothing and lets soak-testers iterate.
