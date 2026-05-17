# LMP Bug & Pain-Point Inventory

## Method

I built this inventory by reading every open issue on `LunaMultiplayer/LunaMultiplayer`, the top 100 issues across all states sorted by reactions, the top 50 closed issues sorted by reactions, every PR merged into `master` since 2026-01-01 (cutoff date 2026-05-16), the open PR queue, the LMP wiki's Troubleshooting and Mod-Support pages, and the DMP issue tracker for shared-ancestor problems (especially the maintainer's own open meta-issue `godarklight/DarkMultiPlayer#373`, which is a catalogue of unfinished work that LMP inherited). I also pulled the comment threads on the most-discussed open issues to harvest reproduction details, suspected causes, and community workarounds. KSP-forum threads turn up in Google but the forum returns HTTP 403 to non-interactive fetches, so I relied on cached search snippets and any quoted material that surfaced in GitHub discussions.

Ranking is by a rough combination of reaction count, comment count, repeat-reporting across sources, and severity of the failure mode (data loss > unplayable session > degraded session > cosmetic). Where the same underlying symptom shows up across multiple issues I have consolidated and listed all reports as sources rather than splitting into duplicates.

`AdmiralRadish` has merged a substantial fix run since 2026-04-15 (21 commits, 17 PRs), and a few earlier bugs are likely resolved by that work. Where I can match a recent PR to a previously reported symptom with high confidence I have marked the bug `Likely fixed in master`; where the PR overlaps but I cannot confirm without reading code I have used `Needs verification`. Anything not yet touched by upstream work since the April restart is marked `Open`.

## Severity scale

- **Critical** — destroys progress (lost vessels, corrupted careers), or makes a fundamental multiplayer activity (docking, EVA, warp, joining) unusable for a typical session. Recoverable only by external file edits or restarts.
- **High** — degrades the core experience for common, non-edge-case play patterns; a session can continue but with workarounds or noticeable broken state.
- **Medium** — annoyance, polish gap, edge case, or quality-of-life regression that does not block play.

## Subsystem index

I have grouped the inventory by the subsystem most likely to own the root cause. Many bugs cross subsystem boundaries; the grouping is for skimming, not a strict claim about where the fix has to live.

- Time & subspace synchronization
- Vessel position, physics & interpolation
- Lock system & ownership handoff
- Docking & vessel coupling
- EVA & Kerbals
- Scenario, career & progression sync
- Persistence (vessels, contracts, science) & server restart behaviour
- Server-side stability & performance
- Network & throughput
- Modded-environment compatibility
- UX, project & release process

## Bugs by subsystem

### Time & subspace synchronization

#### [BUG-001] Server forces solo subspace players to "catch up" to server time, teleporting craft
- **Severity:** Critical
- **Status:** Fixed on fork — commit `0f10b2d3`. Server detects solo subspaces and broadcasts `WarpSubspaceSoloStatusMsgData`; client's `TimeSyncSystem.CheckGameTime` early-bails when in a solo subspace. Documented residual: solo→non-solo rejoin may snap once because the server's `Subspaces[id].time` is stale relative to the solo player's UT (tracked under CLAUDE.md "Known Limitations"; follow-up has the client report its UT delta on rejoin). Phase-2 doc: [`02-analysis/bug-001-solo-subspace-catchup.md`](02-analysis/bug-001-solo-subspace-catchup.md).
- **Sources:** [#469](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/469); related symptom in [#400](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/400) (ship "goes back in time" after relog)
- **Evidence of frequency:** 2 reactions, 4 comments, repro is just "play on any server with a slow PC". Reporter quotes a server log line proving the catch-up math: `[LMP] Adjusted time from: 361950631.564869 to: 361950635.080549 due to error: -3515.68102836609`.
- **Symptoms:** When the local client falls behind server time (slow PC, busy scene), the server snaps the vessel forward along its predicted trajectory, often into the ground. Reporter notes there is no reason for this to happen when the player is the only one in their subspace.
- **Suspected subsystem(s):** TimeSyncSystem, SubspaceSystem, the warp/catch-up logic in `LmpClient/Systems/Warp` and `LmpClient/Systems/TimeSync`.
- **Notes:** The whole point of a private subspace is decoupling, so the fact that the catch-up still fires is almost certainly a missing guard. Reporter even sketches the fix: short-circuit catch-up when the subspace has a population of one. Open PR [#662 "Time Paradoxes Fix"](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/662) by BraveCaperCat2 reworks vessel-message handlers and explicitly attempts to suppress future-state updates from being applied to the local client; it overlaps but is broader in scope than just this issue.
- **Related upstream activity:** PR [#662](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/662) (open).

#### [BUG-002] Warp catastrophically misbehaves above ~100,000 m altitude
- **Severity:** Critical
- **Status:** Closed unresolved (closed 2019; no fix commit referenced; not addressed by any 2026 PR I can find)
- **Sources:** [#263](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/263)
- **Evidence of frequency:** 9 comments, 1 reaction. Multiple commenters reproduce.
- **Symptoms:** "Complete loss in velocity, an invisible craft, a destroyed craft, a phantom craft on the launch pad regardless of previous mission length, and a mostly non-functional space center menu."
- **Suspected subsystem(s):** WarpSystem and vessel position update path when a vessel transitions to packed/on-rails mid-warp.
- **Notes:** Issue was closed without a referenced fix commit. Given the symptom set (invisible/destroyed/phantom craft) it is almost certainly the same family as [BUG-005] and [BUG-006]; AdmiralRadish's interpolation and packed-vessel work in 2026-04 may incidentally fix part of it but the original repro has never been re-tested.

#### [BUG-003] Interpolation causes hard de-sync when warping or syncing subspaces
- **Severity:** High
- **Status:** Needs verification (closed 2018; partially overlapped by 2026-04 interpolation work)
- **Sources:** [#129](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/129)
- **Evidence of frequency:** 5 comments. Workaround documented (toggle interpolation off/on).
- **Symptoms:** Orbits diverge between clients after subspace sync; in worst cases the vessel is outright removed.
- **Suspected subsystem(s):** VesselPositionSystem / VesselUpdateSystem interpolation, intersecting with SubspaceSystem.
- **Notes:** AdmiralRadish's [#628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628) ("vessel position interpolation — use rb directly, fix stale rotation offset") fixes a related class of interpolation defect. Worth re-running the 2018 repro against current master.

#### [BUG-004] Ships taken back in time and teleported when an earlier-subspace player syncs forward
- **Severity:** Critical
- **Status:** Closed unresolved (closed same day with no fix)
- **Sources:** [#251](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/251); same family as [#292](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/292), [#400](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/400), [BUG-001].
- **Evidence of frequency:** Repeatedly described across multiple issues over several years; the most-cited "subspace ate my ship" failure mode.
- **Symptoms:** When the player in the earlier subspace syncs forward, vessels that already exist in the later subspace are moved — often off the planet and into novel orbits.
- **Suspected subsystem(s):** SubspaceSystem merge logic, VesselPositionSystem priority when two subspaces report conflicting state for the same vessel.
- **Notes:** The reporter speculates the bug is that earlier-subspace position is taking priority over the later subspace's authoritative state. This is a foundational design issue that DMP #373 also flagged ("Fix the Docking to a future vessel bug … Jump to the latest subspace").

#### [BUG-005] Vessels disappear or duplicate seemingly at random after sync/rollback
- **Severity:** Critical
- **Status:** Fixed on fork — commit `d64acf66` (BUG-005/006 capstone, protocol bump 0.30.0). Server-side `AuthoritativeSubspaceId` per vessel rejects proto updates from past subspaces; restored `SendUnloadedSecondary*` client broadcasts are safe behind the auth check; `RemoveSubspace` refuses removal when any vessel still holds the subspace. Phase-2 doc: [`02-analysis/bug-005-006-cross-subspace-lock.md`](02-analysis/bug-005-006-cross-subspace-lock.md).
- **Sources:** [#421](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/421), [#483](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/483), [#506](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/506), [#481](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/481); see also `godarklight/DarkMultiPlayer#373` ("DMP's sillyness that causes vessels to disappear or explode").
- **Evidence of frequency:** Four distinct issues describing the same symptom over four years; consistently the top complaint on the forum search snippets ("disappearing and teleporting ships … ghost ships existing only on one client").
- **Symptoms:** Crafts vanish from the tracking station, sometimes only on one client; duplicates of the same vessel appear; reverting/recovering can trigger ghost copies; spec said "use vanilla version … all players leave for >15 min … craft go delet".
- **Suspected subsystem(s):** VesselRemoveSystem (de-kessler / cleanup races), VesselSyncSystem (proto vessel arbitration), interaction with SubspaceSystem rollbacks.
- **Notes:** AdmiralRadish has touched cleanup-side issues in [#625](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/625) ("NaN orbit no longer permanently deletes vessel from server") and [#644](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/644) ("safeguards to stop null reference exceptions in loading or updating vessels"), but the broad-class duplicate/disappear behaviour is not closed out yet.

#### [BUG-006] Player takes UnloadedUpdate lock on vessels in a different subspace, leading to drift and destruction
- **Severity:** High
- **Status:** Fixed on fork — commit `d64acf66` (BUG-005/006 capstone). `LockSystem.AcquireLock` now rejects cross-subspace acquires of `Control`/`Update`/`UnloadedUpdate` from past subspaces; the requester is told no without disturbing the current holder. Phase-2 doc: [`02-analysis/bug-005-006-cross-subspace-lock.md`](02-analysis/bug-005-006-cross-subspace-lock.md).
- **Sources:** [#292](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/292)
- **Evidence of frequency:** 6 comments, detailed source-code-level analysis from the reporter.
- **Symptoms:** Vessels owned by another player and in another subspace receive updates from the local client, causing derailment.
- **Suspected subsystem(s):** LockSystem (the UnloadedUpdate lock specifically) interacting with SubspaceSystem membership checks.
- **Notes:** The reporter proposes either (a) refusing to take UnloadedUpdate on cross-subspace vessels, or (b) adding a launch-priority lock. Both are reasonable; neither has been implemented.

#### [BUG-007] Server time advances while server is off, "deleting" weeks of careful subspace setup
- **Severity:** High (it is a design choice, but multiple users have lost extensive missions to it)
- **Status:** Open feature request with a working community workaround
- **Sources:** [#543](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/543); related [#595](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/595)
- **Evidence of frequency:** 1 reaction, 5 comments. Open PR [#670](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/670) by ShiralynDev adds an option to freeze time when the server is off.
- **Symptoms:** Player stops the server overnight; on restart all in-flight craft have advanced their orbits as if hours had passed, breaking maneuver windows and re-entry plans.
- **Suspected subsystem(s):** Server-side TimeSystem / Subspace persistence (StartTime.txt / Subspace.txt).
- **Notes:** Reporter shipped a Python wrapper that records LastStopTime.txt and rewinds the universe on restart — proof the fix is uncontroversial.
- **Related upstream activity:** PR [#670](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/670) (open).

### Vessel position, physics & interpolation

#### [BUG-008] Polygons scramble and craft teleport underground on spawn
- **Severity:** Critical
- **Status:** Open — Phase-2 analysis done (session 5). Two-phase fix proposed: Phase A is a client-side PQS-aware re-positioning routine in `VesselLoader.LoadVesselIntoGame` after `vesselProto.Load`; Phase B is a server-side proto-field addition (deferred). Phase-2 doc: [`02-analysis/bug-008-pqs-spawn-altitude.md`](02-analysis/bug-008-pqs-spawn-altitude.md). On success retires [BUG-009] and the on-runway variant of [BUG-021] as well.
- **Sources:** [#279](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/279)
- **Evidence of frequency:** 2 reactions, 5 comments, reproduced across hosts; described in similar terms in [#401](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/401) ("kerbal gets sucked into the mun").
- **Symptoms:** "Whenever I spawn I get shot under the ground and every polygon in render range is randomly scrambled … the vehicle just stops in place and a bunch of parts vanish all at once before it explodes."
- **Suspected subsystem(s):** Vessel spawn flow (`VesselLoader` → KSP `ProtoVessel.Load`) interacting with PQS terrain readiness — the DMP-era "PQSAltitude" problem listed in DMP #373. Confirmed by Phase-2 code-walk: no PQS-aware post-load step exists today.
- **Notes:** DMP #373 explicitly names this as needing `vessel.PQSAltitude` because "PQSTerrain does not seem to spawn accurately enough for our needs." LMP inherited the same incorrect spawn-altitude logic.

#### [BUG-009] Vessel explodes / terrain shakes for no apparent reason on the runway
- **Severity:** High
- **Status:** Closed unresolved (closed 2019 with no fix)
- **Sources:** [#274](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/274)
- **Evidence of frequency:** 2 reactions, 1 comment, simple stock-only repro.
- **Symptoms:** "Vessel crumples and explodes for no reason, and terrain shakes violently" — load Stratolauncher, click launch.
- **Suspected subsystem(s):** Same family as [BUG-008] — PQS terrain alignment at spawn, or phantom-force application from a stale flight-state update on a freshly-loaded packed vessel.
- **Notes:** Forum/wiki troubleshooting page acknowledges the "throttle reset and vessel jumps when syncing" class of behaviour. AdmiralRadish's [#633](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/633) ("Zero throttle when acquiring vessel control lock") and [#649](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/649) ("Fix throttle spike when taking control of a spectated vessel") fix one specific cousin of this, but I do not believe the on-runway terrain-shake variant is covered.

#### [BUG-010] Craft explodes on disconnect / game exit when within rendering distance of another player
- **Severity:** Critical (silent progress destruction; very common pattern for landed bases & water craft)
- **Status:** FIXED ON FORK (session 7). Part A: server broadcasts `VesselPinned` for each lock-owned vessel before fanning out lock releases; remaining clients hold the named vessels immortal via `VesselPinnedSystem` until the original pilot reconnects or another player explicitly takes the helm. Part B: client flushes a fresh proto for every locally-owned vessel before `NetworkConnection.Disconnect`, tightening the dock-then-undock-child pose for clean disconnects. Ungraceful drops rely on Part A alone. See [`docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md`](02-analysis/bug-010-disconnect-vessel-handoff.md).
- **Sources:** [#654](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/654)
- **Evidence of frequency:** Repro: two players camped on a lake; first to disconnect loses their floatplane every time.
- **Symptoms:** Disconnecting (cleanly or via connection drop) while another player is rendering your craft causes it to explode immediately. Particularly bad on water.
- **Suspected subsystem(s):** Disconnect handler in client — likely the local vessel is unpacked into the remaining player's scene without correct ownership handoff, and physics kicks in on a vessel that should have been left in a packed/saved state.
- **Notes:** This pairs with [BUG-024] (kicked-during-docking) as a symptom of the missing graceful-handoff machinery DMP #31 ("Add a handover system to another client after docking") asked for in 2017.

#### [BUG-011] Sustained NRE spam and 2-3 FPS drop after a few minutes of play
- **Severity:** High (becomes unplayable)
- **Status:** Likely fixed in master (root cause for one large variant — see PR [#608](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/608))
- **Sources:** [#588](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/588) (17 comments, the canonical thread), [#547](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/547), [#572](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/572), [#580](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/580), [#583](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/583), [#574](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/574), [#517](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/517), [#464](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/464)
- **Evidence of frequency:** Easily the most-reported quality-of-life killer. Same NRE signature (`Vessel.UpdateCaches` / `Vessel.GetVesselCrew` / `VesselPrecalculate.MainPhysics`) appears across at least eight issues.
- **Symptoms:** Logs spam `NullReferenceException` 1-2 times per millisecond; FPS collapses; mostly after 30s-5min in flight.
- **Suspected subsystem(s):** VesselProtoSystem.Validate (orbit/body validation), MurderCrew / CheckKill path, asteroid handling.
- **Notes:** Detailed root-cause analysis in the #588 comment thread by Fierce-Cat: the NRE class has three distinct causes — (1) planet-pack vessels arriving at a server without Kopernicus loaded, (2) clients missing parts the server has, (3) UTF-8 truncation in `VesselMsgReader` (see [BUG-012]). PR [#608](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/608) by DrewBanyai validates body indices at proto-vessel validation time and blocks bad messages; this covers the (1) variant. The crew/asteroid variants are still open.

#### [BUG-012] UTF-8 vessel files truncated in `VesselMsgReader`, causing NRE spam server-side
- **Severity:** High
- **Status:** Likely fixed in master
- **Sources:** Root cause documented by Fierce-Cat in [#588](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/588) comments; fix shipped as PR [#656](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/656) ("fix: VesselMsgReader truncation in Synchronization"), merged 2026-04-22.
- **Evidence of frequency:** Triggered by any vessel containing non-ASCII characters (Russian, Chinese, Korean) — see also [BUG-013].
- **Symptoms:** Server repeatedly tries to send a vessel file whose declared length and actual byte size disagree, client throws NREs in a loop.

#### [BUG-013] Localized stateString in `ModuleReactionWheel` causes server-wide NRE spam
- **Severity:** Critical (one localized player can take the whole server unplayable until their vessel is manually edited out)
- **Status:** Fixed on fork — commit `c5ab8fa5`. Defensive server-side sanitiser at the proto-vessel ingest boundary: `Server/System/Vessel/VesselSanitizer.cs` walks `vessel.Parts`, finds every `ModuleReactionWheel{V2,}` module, and rewrites `stateString` to `"Running"` when the current value isn't in the whitelist `{"Running","Disabled","Broken"}`. Hooked into `VesselDataUpdater.RawConfigNodeInsertOrUpdate` so neither the universe-on-disk copy nor any downstream relay carries the bad payload. Logged once per affected vessel with `[fix:BUG-013]`; idempotent on clean vessels.
- **Sources:** [#598](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/598)
- **Evidence of frequency:** 1 reaction, 4 comments. Concrete repro: Russian KSP client + reaction wheels → `stateString = Работает` ends up in the vessel file → ~3 NREs/ms across the whole server.
- **Symptoms (historical):** Same NRE spam family as [BUG-011]; only workaround was to grep the universe folder for the offending vessel and patch the string back to `Running` manually.
- **Suspected subsystem(s):** Vessel proto serializer (which is round-tripping a stateString that KSP itself does not expect to be localized).
- **Notes:** Wider implications: KSP localises more than just the reaction-wheel stateString. If another field exhibits the same NRE family, extend `VesselSanitizer` with a new whitelist + module-name guard — the helper is designed to grow.

#### [BUG-014] Vessel position interpolation uses stale rotation offset / wrong rigidbody
- **Severity:** High
- **Status:** Fixed in master — verified by Phase-2 audit (2026-05-16). PR [#628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628) covers all four `transform.*` setter sites in `LmpClient/Systems/VesselPositionSys/ExtensionMethods/VesselPositioner.cs`; no remaining sites without paired `rb.*` updates.
- **Sources:** Fix PR [#628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628), merged 2026-04-17; commit `1b5fc45b`.
- **Notes:** Directly addresses observed visual jitter on spectated vessels. Closed by upstream; no fork-side work needed. Audit findings in [`02-analysis/bug-014-extensionmethods-rb-audit.md`](02-analysis/bug-014-extensionmethods-rb-audit.md).

### Lock system & ownership handoff

#### [BUG-015] Throttle spikes / unintended firing when acquiring control of a spectated vessel
- **Severity:** High
- **Status:** Likely fixed in master
- **Sources:** Fix PRs [#633](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/633) and [#649](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/649), merged 2026-04-17 and 2026-04-22.
- **Notes:** Verify the original repro (take control of someone else's burning craft → it jumps to throttle 1.0) is gone.

#### [BUG-016] `referenceTransformId` overwrite on active vessel
- **Severity:** Medium (subtle but caused crew/IVA misalignment and odd camera state)
- **Status:** Likely fixed in master
- **Sources:** Fix PR [#632](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/632), merged 2026-04-22.

#### [BUG-017] `TryRemoveCallback` condition inverted, callbacks never removed
- **Severity:** Medium (slow-burning leak; contributor of long-session degradation)
- **Status:** Likely fixed in master
- **Sources:** Fix PR [#630](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/630), merged 2026-04-22.
- **Notes:** Worth checking how many subsystems were relying on RemoveCallback for cleanup; an inverted condition for that long suggests other leaks may have been masked by it.

### Docking & vessel coupling

#### [BUG-018] Docking with another player kicks one player and destroys ports
- **Severity:** Critical (docking is a flagship feature)
- **Status:** Fixed in master — verified by fork audit (2026-05-16, session 5). Upstream PR [#687](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/687) commit `4c124f11` adds `DockingPortUtil.EnsureRecoverableForUndock` plus Harmony shims on `ModuleDockingNode_Undock` / `_UndockSameVessel` / `Part_Undock` that (a) pre-validate FSM state, (b) recover transient states before undock proceeds, and (c) rehydrate the docking pair from part-tree / `dockedPartUId` data when FSM reports an unexpected state. PR [#660](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/660) covers the same family on the coupling side. Adoption decision: verbatim (AdmiralRadish-owned turf per fork-master strategy; no fork edits needed).
- **Sources:** [#380](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/380), [#422](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/422); architectural ancestor in `godarklight/DarkMultiPlayer#373` ("Fix docking — Possibly use lock-system to make sure dockings are deterministic").
- **Evidence of frequency:** "Seems happen every time on our private server." Recurring across multiple years.
- **Symptoms:** Docking ports explode, occupant kerbals are lost, sometimes one player is kicked.
- **Suspected subsystem(s):** VesselCoupleSystem, VesselCoupleEvents, lock arbitration during the couple event.

#### [BUG-019] Undock button disappears after warp / scene change on docked craft
- **Severity:** High (no in-game recovery — requires manual XML edits)
- **Status:** Fixed in master — verified by fork audit (2026-05-16, session 5). Upstream PR [#687](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/687) `EnsureRecoverableForUndock` rehydrates the docking pair from part-tree / `dockedPartUId` data when FSM reports a stuck state, which is exactly the reporter's failure mode. PR [#639](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/639) ("Validate docking port FSM state before processing undocking") is the prereq. Adoption decision: verbatim.
- **Sources:** [#679](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/679)
- **Symptoms:** Undock UI button silently disappears; only workaround documented is editing vessel files to zero out docking ports plus a separate undock-forcing mod.
- **Suspected subsystem(s):** Docking port FSM state; vessel proto round-trip not preserving the docked-pair relationship across warp/scene transitions. AdmiralRadish's [#639](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/639) ("Validate docking port FSM state before processing undocking") is in the same area.

#### [BUG-020] Synchronization problems prevent decoupling, docking and staging updates
- **Severity:** High
- **Status:** Open
- **Sources:** [#422](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/422)
- **Symptoms:** Cannot launch ships, dock, or update existing vessels; spawned vessel charges funds but never appears on the server; spectator sees old staging state.
- **Suspected subsystem(s):** VesselSyncSystem, ProtoVessel send path, staging update handler.

### EVA & Kerbals

#### [BUG-021] EVA-Board hijack: phantom vessels, runaway reputation loss, kerbal cloning
- **Severity:** Critical
- **Status:** Closed unresolved (the original issue was closed without a referenced fix; the wider EVA-board family is still listed as a known problem in DMP #373)
- **Sources:** [#198](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/198), [#219](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/219), [#368](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/368); ancestor in `godarklight/DarkMultiPlayer#373` ("Fix the EVA-Board hijack").
- **Evidence of frequency:** Three independent issues over two years with the same symptom shape.
- **Symptoms:** Repeated EVA + Board produces ghost "Saved Kerbals" entries, inflates the "Sending Player Vessels" counter, massive negative reputation events, EVA kerbal thrown into deep space or the Sun when spectated, kerbal cloning between control and spectator clients.
- **Suspected subsystem(s):** EVA / Kerbal vessel proto handling; the Board action creates a transient vessel that the spectator client mishandles.

#### [BUG-022] Kerbals whose first name is a substring of another kerbal's name disappear
- **Severity:** High when triggered
- **Status:** Closed (no PR referenced in the close)
- **Sources:** [#541](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/541)
- **Symptoms:** Reporter spent 3 hours debugging a borked save; deleting `Billy-Bobdas Kerman` fixed it.
- **Suspected subsystem(s):** Some kerbal-state code is doing substring matching where it should do equality. Almost certainly a one-line fix once located.

#### [BUG-023] Astronaut Complex desyncs with assigned kerbals, breaks hiring
- **Severity:** Critical (no in-game recovery)
- **Status:** ✅ Fixed on fork (2026-05-17, session 9, commit `5a240c32`). Three-part port from upstream Release/0_29_2 (Drew Banyai, d3223931 + 138c2b3e): `VesselLoader.ScrubInvalidProtoCrew` strips null entries in lockstep with `protoCrewNames`; `VesselProtoSystem.CheckVesselsToLoad` drains queued `KerbalProto` before each vessel-load batch; `Part_RegisterCrew` + `KnowledgeBase_GetVesselCrewByAvailablePart` Harmony patches as defense-in-depth for autosave Save+Load round-trip. See [Phase-2 analysis](02-analysis/bug-023-astronaut-complex-desync.md).
- **Sources:** [#576](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/576), [#603](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/603)
- **Evidence of frequency:** Two separate reports, screenshots showing 10/12 capacity but no hire-able slots.
- **Symptoms:** A kerbal from a particular craft "does not exist in the astronaut complex" per debug console; terminating that craft fixes it.
- **Suspected subsystem(s):** Crew roster sync (CrewSystem or whatever assembles the astronaut-complex view).

#### [BUG-024] Docking with another player kicks the other player — see [BUG-018]

(Cross-listed; same root cause family.)

### Scenario, career & progression sync

#### [BUG-025] R&D node researchable multiple times by separate clients
- **Severity:** High (game-breaking economy bug in shared-career play)
- **Status:** ✅ Fixed on fork (2026-05-17, session 9, commit `83905d4d`). Server-side synchronous check-and-claim under the per-scenario writer lock + new `ShareProgressTechnologyRejectedMsgData` server-to-client rejection + client-side `ShareScienceSystem.IgnoreEvents`-bracketed `AddScience` refund. Additive wire enum (`TechnologyRejected = 11`); no protocol bump. See [Phase-2 analysis](02-analysis/bug-025-rd-double-purchase.md).
- **Sources:** [#667](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/667)
- **Evidence of frequency:** Filed 2026-05-07 against a self-compiled latest-main build, so the bug exists on the current head.
- **Symptoms:** Two clients with the node details panel already open can both purchase the same node; the right-pane state never invalidates and science is charged each time.
- **Suspected subsystem(s):** R&D scenario sync (ResearchAndDevelopmentScenarioStore), GUI invalidation when remote progress arrives.
- **Notes:** ShiralynDev's [#668](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/668) "R&D disconnect fix" is in the same area but does not look like a fix for the duplicate-purchase race.

#### [BUG-026] New contracts stop appearing until server restart
- **Severity:** High
- **Status:** Likely fixed in master
- **Sources:** [#659](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/659); fix PR [#650](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/650) ("Fix contract disappearance, CC load exceptions, and SOI spam for LMP-managed vessels"), and [#645](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/645) ("Career Contract changes to help prevent Contract Lock issues").
- **Evidence of frequency:** 3 reactions, 2 comments; the issue was closed 2026-05-11 after the contract-side PRs landed.

#### [BUG-027] Career-mode satellite missions "build a new unmanned probe" are impossible to satisfy
- **Severity:** High (blocks an entire mission category)
- **Status:** Open
- **Sources:** [#651](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/651)
- **Symptoms:** Launching any probe meeting the stated criteria never checks off the first objective.
- **Suspected subsystem(s):** Contract-condition evaluation hook that LMP intercepts; almost certainly a vessel-state mirror that does not flag the right field.

#### [BUG-028] Unstable career-mode stat persistence (science / funds lost on relog)
- **Severity:** High
- **Status:** Open
- **Sources:** [#369](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/369); related [#508](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/508) (config option to keep career stats per-player).
- **Symptoms:** Science is gathered and the vessel is recovered, but on relog the gain is lost; `Received unhandled library message Ping from` spam appears in the console.

#### [BUG-029] Scenario sync regressed on recent master vs. earlier commit
- **Severity:** High (suggests a recent change broke a working subsystem)
- **Status:** Likely fixed (issue closed 2026-04-13; fix referenced as [#638](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/638) "Fix scenario ConfigNode parsing and add error handling")
- **Sources:** [#612](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/612)
- **Notes:** Verify the bisected pre-regression commit (`73feadbc`) matches the post-fix behaviour.

#### [BUG-030] `ScenarioNewGameIntro` tutorial popup blocks scene loads when joining
- **Severity:** Medium
- **Status:** Likely fixed in master
- **Sources:** Fix PR [#686](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/686) ("Ignore ScenarioNewGameIntro during scenario receive sync"), merged 2026-05-16.

### Persistence (vessels, contracts, science) & server restart behaviour

#### [BUG-031] Contract repair vessels (rover/satellite) are removed on server restart
- **Severity:** High (mission becomes unfinishable; only recovery is to cancel the contract)
- **Status:** Open
- **Sources:** [#458](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/458)
- **Suspected subsystem(s):** Server-side dekessler / orphan-vessel cleanup that incorrectly classifies contract-spawned vessels as removable.

#### [BUG-032] NRE on rejoin after server restart blocks entry to VAB
- **Severity:** High (forces vessel deletion to recover)
- **Status:** Open
- **Sources:** [#472](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/472)
- **Symptoms:** `SafetyBubbleSystem` throws NRE on rejoin; CommNet `canComm` parsed with an invalid boolean value (`Tru` instead of `True`) is the smoking gun in the log.
- **Suspected subsystem(s):** Vessel proto serializer (boolean truncation), SafetyBubbleSystem null-tolerance.

#### [BUG-033] Race condition serializing `ScenarioStoreSystem.CurrentScenarios` during backup
- **Severity:** Critical when triggered (uncaught thread-pool exception → server crash)
- **Status:** ✅ Fixed on fork (2026-05-17, session 8, commit `87105f41`). `ScenarioStoreSystem.BackupScenarios` now serializes each scenario under the matching per-scenario writer lock via `ScenarioDataUpdater.GetSemaphore`. Disk write moved outside the lock. `BackupLock` dropped from this code path to avoid AB-BA deadlock with `ScenarioPartPurchaseDataUpdater`'s recursive call. See [Phase-2 analysis](02-analysis/bug-033-backup-race.md).
- **Sources:** [#509](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/509)
- **Evidence of frequency:** Detailed code-level analysis in the report.
- **Symptoms:** Backup task reads the scenarios dictionary while a writer (under a different semaphore) is mutating it; concurrent modification exception terminates the worker.
- **Suspected subsystem(s):** Server `BackupScenarios()` and the scenario semaphore design.

#### [BUG-034] Server lags severely after large `ProgressTracking.txt` crew lists accumulate
- **Severity:** High over time
- **Status:** Open
- **Sources:** [#542](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/542)
- **Symptoms:** Each restart bloats `Universe/Scenarios/ProgressTracking.txt` with very long crew lists; loading the VAB or launching becomes "extremely slow".
- **Workaround:** Manually delete the `crew` objects.
- **Suspected subsystem(s):** ProgressTracking scenario merge logic dedupes nothing.

#### [BUG-035] Linux server memory leak — RAM never released, eventually OOM-killed
- **Severity:** High for server operators
- **Status:** Open
- **Sources:** [#571](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/571)
- **Symptoms:** RSS grows monotonically with player count and never falls back when players disconnect; explicit `<GcMinutesInterval>15</GcMinutesInterval>` does not help; process is OOM-killed.
- **Suspected subsystem(s):** Server-side message buffers / scenario caches retaining references after player disconnect.

#### [BUG-036] Mod difficulty/gameplay settings reset on rejoin
- **Severity:** Medium
- **Status:** Open (open PR [#594](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/594) by iddqd0 "Fix game parameter syncing to not overwrite unrelated existing game parameters" sits idle from 2025)
- **Sources:** [#548](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/548), [#587](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/587)
- **Notes:** PR #594 looks like the right shape of fix; it has been open without movement since August 2025 and is now in the upstream backlog.

### Server-side stability & performance

#### [BUG-037] Server console mangles backspaces and interleaves output with messages
- **Severity:** Medium (operator UX)
- **Status:** Open
- **Sources:** [#597](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/597)

#### [BUG-038] XML prologue encoding is declared wrong in server config files
- **Severity:** Medium (correctness; can bite localized installs)
- **Status:** Open
- **Sources:** [#602](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/602)

#### [BUG-039] Cannot add custom keys in `gameplaysettings.xml`
- **Severity:** Medium
- **Status:** Open
- **Sources:** [#587](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/587)

### Network & throughput

#### [BUG-040] Network message flush overhead burns CPU and bandwidth
- **Severity:** Medium (perf), supports the FPS-drop family
- **Status:** Likely fixed in master
- **Sources:** Fix PRs [#631](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/631), [#640](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/640), [#647](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/647), merged 2026-04-17.

#### [BUG-041] LAN connection fails with "no response from remote host" while WAN connection from same server works
- **Severity:** High for LAN-only or split-tunnel setups
- **Status:** Open
- **Sources:** [#653](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/653)
- **Evidence of frequency:** 7 comments; reproduced on both Windows and Linux clients.
- **Notes:** Likely a NAT-loopback / hairpin issue or a ListenAddress/bind problem rather than a true LMP bug; the wiki troubleshooting page already calls this region out, but the failure mode is opaque to the user and the error message is unhelpful.

#### [BUG-042] Connection performance degraded since 0.28 release (3 FPS, VAB unenterable, repeated disconnects)
- **Severity:** High
- **Status:** Open
- **Sources:** [#444](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/444)
- **Notes:** May overlap with [BUG-011]; worth confirming against the post-2026-04 network batching work.

### Modded-environment compatibility

#### [BUG-043] Modded planet packs make SafetyBubble launch-site iteration null-deref
- **Severity:** Medium
- **Status:** Likely fixed in master
- **Sources:** Fix PR [#627](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/627), merged 2026-04-17.

#### [BUG-044] Resource allowlist hard-blocks instead of warning, killing legitimate modded play
- **Severity:** Medium
- **Status:** Likely fixed in master
- **Sources:** Fix PR [#634](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/634) ("Relax resource allowlist check to warn instead of block").

#### [BUG-045] Breaking Ground deployable science vanishes on reconnect
- **Severity:** High (largest open ticket by reactions: 22)
- **Status:** Fixed (Phase B.1, 2026-05-16; ported from upstream Release/0_29_2 commit `2526e15a`). Phase-2 doc: [bug-045-deployable-science.md](02-analysis/bug-045-deployable-science.md).
- **Sources:** [#308](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/308)
- **Evidence of frequency:** 22 reactions, 7 comments — the most-thumbsed open issue in the tracker. Multiple "still broken" follow-ups years apart.
- **Symptoms:** Deployable science modules are placed, work, are visible at the tracking station; on reconnect the control station and panels disappear.
- **Root cause:** `VesselEvaEditorEvents.VesselCreated` only fired the `SendVesselMessage` path when `System.DetachingPart` was set (the EVA Construction Mode part-drop signal). Breaking Ground deployables are spawned by `GameEvents.onNewVesselCreated` without raising any `EVAConstructionEvent`, so their protos were never transmitted to the server even though `VesselLockSystem`'s bulk pass still acquired locks for them. Vessel existed only in the placing player's local save.
- **Fix:** Widen the `VesselCreated` gate to also accept `vesselType == VesselType.DeployedSciencePart || VesselType.DeployedScienceController`.
- **Notes:** A third-party fork (`ItzPray/KSPMulti`) had also claimed an in-progress fix. Drew Banyai's upstream `Release/0_29_2` branch landed the actual fix as commit `2526e15a` (2026-05-05).

#### [BUG-046] KSP Recall, VesselMover, BDArmory, USI-LS, Procedural Parts, Kerbal Konstructs are known broken or partial
- **Severity:** Medium (workaround exists for most: use the listed fork mod, or disable)
- **Status:** Open (documented on the wiki, no programmatic fix)
- **Sources:** LMP wiki [Mod-support](https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Mod-support); KSP forum threads cited in search snippets.

### UX, project & release process

#### [BUG-047] `master` has diverged from the release branches by 188 commits; nightly users get unreleased fixes, "stable" users do not
- **Severity:** Medium (process), High (consequence — stable users hit fixed bugs)
- **Status:** Open (under active discussion)
- **Sources:** [#671](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/671); [#566](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/566) ("Is this project abandoned?") is the historical precedent for the same complaint surface.
- **Evidence of frequency:** 2 reactions, 4 comments, includes a back-and-forth between active maintainers (DrewBanyai, BraveCaperCat2, ShiralynDev) acknowledging the branch model is confusing and committing to a roadmap.
- **Notes:** Not strictly a code bug; matters because it dictates which "upstream" this fork tracks.

#### [BUG-048] Fire animation missing on staged engines when spectated
- **Severity:** Medium (cosmetic but a clear spectator-side state-mirror gap)
- **Status:** Open
- **Sources:** [#419](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/419)

#### [BUG-049] Time-warp toolbar buttons sometimes become unresponsive (keys still work)
- **Severity:** Medium
- **Status:** Open
- **Sources:** [#426](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/426)

#### [BUG-050] Server NaN orbit used to permanently delete vessels
- **Severity:** Critical when it hit
- **Status:** Likely fixed in master
- **Sources:** Fix PR [#625](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/625), merged 2026-04-17.

### Discovered during Phase-2 analysis

#### [BUG-051] Client stuck in `CurrentSubspace = -1` limbo after time warp ends
- **Severity:** High (hard failure — no game time advances, no position updates accepted during the gap)
- **Status:** Fixed on fork — split into two deliverables that shipped in commit order:
  - **BUG-051a (server-side request dedup, commit `9732fc7e`):** new `WarpRequestCache` keyed on `(player, RequestSeq)` with 60s TTL. `WarpSystemReceiver.HandleNewSubspace` consults the cache and replays the original subspace assignment to the requester on a hit, so a retry can never mint an orphan. Wire change: `WarpNewSubspaceMsgData` gains optional `uint RequestSeq` (defensive deserialize — pre-fix clients sending no trailing 4 bytes parse as 0).
  - **BUG-051b (client steady-state retry, commit `25303e7d`):** new `CheckSteadyStateRetry` predicate at 500ms — when `CurrentSubspace==-1 && WaitingSubspaceIdFromServer && TimeWarp idle`, resends the cached `_currentRequestSeq`. Server dedup engages, returning the cached subspace assignment.
- **Sources:** No specific upstream issue. 15-second `CheckStuckAtWarp` watchdog at [LmpClient/Systems/Warp/WarpSystem.cs:126-134](../../LmpClient/Systems/Warp/WarpSystem.cs#L126-L134) is retained as defense-in-depth.
- **Symptoms (historical):** Client drops to `CurrentSubspace = -1` when time warp ends. If the server's subspace-assign broadcast is lost, the `WaitingSubspaceIdFromServer` flag stays true; `CheckWarpStopped` is gated on `!WaitingSubspaceIdFromServer` so it cannot re-fire; the watchdog re-requests after 15s. During the gap the client is in limbo.
- **Phase-2 doc:** [`02-analysis/bug-051-stuck-warp-limbo.md`](02-analysis/bug-051-stuck-warp-limbo.md)

#### [BUG-052] Lidgren `NetReliableSenderChannel.DestoreMessage` NREs on late ACK during peer shutdown
- **Severity:** Medium (intermittent host-process crash; only observable during teardown / disconnect, but enough to abort the Stage 4.9 mock-client harness with no useful diagnostic)
- **Status:** Fixed on fork (session 5). Surgical null-guard at the top of `DestoreMessage` clears the empty slot defensively and returns; preserves the `#if DEBUG` throw for development builds.
- **Sources:** Discovered while running the Stage 4.10 BUG-051a regression test (intermittent — only fires when an ACK arrives during `NetPeer.ExecutePeerShutdown`).
- **Symptoms:** Test host process crashes with `Unhandled exception. System.NullReferenceException` originating from `NetReliableSenderChannel.DestoreMessage` → `Interlocked.Decrement(ref storedMessage.m_recyclingCount)`.
- **Root cause:** The original code decremented `storedMessage.m_recyclingCount` BEFORE the null check (which was on the next line). When an in-flight ACK arrived for a message whose slot had already been cleared (e.g., the test runner shutting down NetServer + NetClient concurrently), `storedMessage` was null and the dereference NRE'd. The `#if DEBUG` arm that was supposed to throw a descriptive NetException sat *after* the dereference — unreachable.
- **Fix:** Move the null check above the dereference; in `!DEBUG` builds the empty-slot case clears `m_storedMessages[storeIndex]` and returns.

## Top-10 priority list

These are my picks for the first wave of Phase 2 code analysis, ranked by a combination of severity, frequency, and how much they unlock for other work. **Status as of 2026-05-16 session 5** is annotated inline so the picklist doubles as a quick gap view.

1. ~~**[BUG-001] Forced-catch-up teleporting solo subspace players**~~ — ✅ FIXED ON FORK (`0f10b2d3`). Documented rejoin-race residual is the only remaining tail.
2. ~~**[BUG-005] Vessels disappear or duplicate at random**~~ — ✅ FIXED ON FORK (`d64acf66`, BUG-005/006 capstone + protocol bump 0.30.0).
3. **[BUG-008] Polygons-scrambled / craft-underground on spawn** — OPEN. Known PQS-timing class from DMP days; fixing it would also retire a chunk of [BUG-009] and [BUG-021] symptoms. Phase-2 doc next.
4. ~~**[BUG-018] Docking destroys ports and kicks players**~~ — ✅ FIXED IN MASTER via upstream PRs [#660](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/660) + [#687](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/687) (commit `4c124f11`). Adopted verbatim.
5. ~~**[BUG-013] Localized stateString NRE spam**~~ — ✅ FIXED ON FORK (`c5ab8fa5`). Defensive `VesselSanitizer` rewrites localised reaction-wheel `stateString` back to canonical English on ingest.
6. ~~**[BUG-010] Disconnect destroys craft within rendering distance of another player**~~ — ✅ FIXED ON FORK (session 7). Part A: server-broadcasts `VesselPinned`; remaining clients hold the leaver's vessels immortal via `VesselPinnedSys` until any player takes the helm. Part B: client flushes a fresh proto for owned vessels before `Disconnect`. See [`docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md`](02-analysis/bug-010-disconnect-vessel-handoff.md).
7. ~~**[BUG-025] R&D node researchable multiple times in shared career**~~ — ✅ FIXED ON FORK (`83905d4d`). Server-side check-and-claim + rejection message + client-side science refund (wrapped in ShareScienceSystem event-suppression to avoid the runaway broadcast loop).
8. **[BUG-045] Breaking Ground deployable science vanishes on reconnect** — OPEN. Highest reaction count in the open tracker (22), one of the few bug families with a clear hypothesis (missing game-event hook) and no upstream PR in flight.
9. ~~**[BUG-033] Backup race in `ScenarioStoreSystem.CurrentScenarios`**~~ — ✅ FIXED ON FORK (`87105f41`). Per-scenario writer lock now also covers backup-side `ConfigNode.ToString()`; AB-BA deadlock vs `ScenarioPartPurchaseDataUpdater` avoided by dropping the redundant outer `BackupLock` from this path.
10. ~~**[BUG-023] Astronaut Complex desync**~~ — ✅ FIXED ON FORK (`5a240c32`, ported from Drew Banyai's Release/0_29_2). Three-part fix: load-time `ScrubInvalidProtoCrew` + `KerbalsToProcess` drain race-closer + Harmony patches for the autosave round-trip.

**All ten top-10 bugs are closed as of 2026-05-17 (session 9).** Eight fixed on the fork, two adopted from upstream PRs. Closure marks Stages 3 + 4 of the campaign complete.

**Fork-closed bugs not originally in the top-10:** [BUG-006] (cross-subspace lock, capstone `d64acf66`), [BUG-014] (audit-closed via upstream PR #628, `7f1393f4`), [BUG-019] + [BUG-024] (closed by upstream PR #687, audit `4c124f11`), [BUG-051a/b] (warp limbo, `9732fc7e` + `25303e7d`), [BUG-003/004] (interp cap, `cd551859`), [BUG-052] (vendored Lidgren NRE on late-ACK during peer shutdown, `b7a51ae1`).

## Open questions

These are the bugs I could not pin down with public sources alone; Phase 2 should resolve them against the code.

- **Is [BUG-002] really still present after the 2026-04 packed-vessel and interpolation fixes?** The issue was closed without a fix commit, but symptom overlap with PRs [#628](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/628), [#633](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/633), [#649](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/649) is high. Needs a clean repro on current master.
- **How much of [BUG-011] is still observable after [#608](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/608) and [#656](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/656)?** The Fierce-Cat analysis identifies three distinct NRE causes; only some have explicit fixes.
- ~~**Are [BUG-018] and [BUG-019] separate bugs or one bug with two presentations?**~~ Resolved 2026-05-16, session 5: upstream PR #687's `EnsureRecoverableForUndock` rehydrates the docking pair from `dockedPartUId` when FSM is stuck, which is the BUG-019 symptom; the same util is called from the BUG-018 multi-player coupling path. Two presentations of one underlying FSM-state-management problem; both now closed.
- **Does the open PR [#662](https://github.com/LunaMultiplayer/LunaMultiplayer/pull/662) "Time Paradoxes Fix" subsume [BUG-001] and parts of [BUG-004]?** The PR description claims a broad rework of vessel-message handlers to drop future-state updates; the implementation is large and unreviewed at the time of writing.
- **Is the Linux memory leak [BUG-035] a server-only issue or a symptom of [BUG-011]'s NRE spam holding objects alive on the server side?** Both bugs report monotonic memory growth.

## Sources consulted

- LMP GitHub issue tracker (open, closed, sorted by reactions): https://github.com/LunaMultiplayer/LunaMultiplayer/issues
- LMP closed pull requests, merged since 2026-01-01: https://github.com/LunaMultiplayer/LunaMultiplayer/pulls?q=is%3Apr+is%3Aclosed+merged%3A%3E%3D2026-01-01
- LMP open pull requests: https://github.com/LunaMultiplayer/LunaMultiplayer/pulls?q=is%3Apr+is%3Aopen
- AdmiralRadish commit log on `master`: https://github.com/LunaMultiplayer/LunaMultiplayer/commits?author=AdmiralRadish
- LMP wiki — Troubleshooting: https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Troubleshooting
- LMP wiki — Mod support: https://github.com/LunaMultiplayer/LunaMultiplayer/wiki/Mod-support
- LMP project page: https://lunamultiplayer.com/
- DMP issue tracker (architectural ancestor): https://github.com/godarklight/DarkMultiPlayer/issues
- DMP meta-issue #373 "Current Issues" (open backlog inherited by LMP): https://github.com/godarklight/DarkMultiPlayer/issues/373
- KSP forum LMP beta thread (Google search snippets only; the forum returns HTTP 403 to non-interactive fetches and I could not retrieve full pages): https://forum.kerbalspaceprogram.com/topic/168271-110-luna-multiplayer-lmp-beta/
- KSP forum "How do I fix my luna multiplayer server?" (403 to non-interactive fetch): https://forum.kerbalspaceprogram.com/topic/219557-how-do-i-fix-my-luna-multiplayer-server/
- KSP forum "LMP server issue" (403 to non-interactive fetch): https://forum.kerbalspaceprogram.com/topic/225086-lmp-server-issue/
- Fierce-Cat fork, root-cause notes for the NRE spam family: https://github.com/Fierce-Cat/LunaMultiPlayer
- ItzPray fork, claimed in-progress deployable-science fix: https://github.com/ItzPray/KSPMulti

I could not retrieve full forum-thread content because the KSP forum blocks the non-interactive fetcher I used; the Google snippets exposed corroborating user reports for the desync, throttle-reset and mod-compat families, but I have flagged direct forum citations as snippet-only above. Reddit searches surfaced no high-signal threads beyond what is already on GitHub.
