# BUG-033 — Race condition serializing `ScenarioStoreSystem.CurrentScenarios` during backup

**Phase-2 analysis. Status: Fixed (2026-05-17, session 8).**

Upstream tracker: [#509](https://github.com/LunaMultiplayer/LunaMultiplayer/issues/509). Critical-when-triggered server-crash class: the periodic backup task serializes a scenario `ConfigNode` while a writer task on a different thread mutates the same node's children list, throwing `InvalidOperationException: Collection was modified` (or worse, writing a partially-serialized fragment to disk that corrupts the next restore).

## Repro

Stochastic — depends on physics timing between a `Share*` writer arriving on the network thread and the periodic backup tick firing. Indirect reports from the linked issue, plus the code-level race is reproducible deterministically in a stress test:

1. Two players are progressing career events (contracts, achievements, R&D node purchases, funds adjustments). Each event arrives as a `ShareProgress*MsgData` and triggers a `ScenarioDataUpdater.Write*DataToFile` task.
2. The periodic `BackupSystem.PerformBackupsAsync` loop (cadence `BackupIntervalMs`, default 30s) eventually fires `RunBackup → ScenarioStoreSystem.BackupScenarios()`.
3. Backup snapshots the scenarios dict and iterates `.ToString()` on each `ConfigNode`. If a writer is mid-`AddNode` / `RemoveNode` / `ReplaceNode` on that same node, the iterator inside `ToString()` collides with the structural mutation and throws.
4. The thrown exception propagates out of `BackupScenarios` → `RunBackup` → `PerformBackupsAsync`'s await chain, taking the backup task with it. Worst case: a partial serialization is already on disk before the throw, leaving the canonical scenario file corrupt; next server restart's `LoadExistingScenarios` fails to parse it.

Likelihood per hour ≈ low under light career activity, but every long-running 4-player career server eventually trips it. The reporter's symptom in [#509] is "server crashes every few days with no useful log line"; the crash log shows the unhandled `InvalidOperationException` originating in `LunaConfigNode.ConfigNode.ToString`.

## Root cause

Three lock objects, three lock domains, no overlap between writers and backup-serialization on the same instance.

| Lock | Defined in | Protects |
|---|---|---|
| `BackupLock` (instance lock) | [Server/System/ScenarioStoreSystem.cs:20](../../../Server/System/ScenarioStoreSystem.cs#L20) | `LoadExistingScenarios`, `ChangeExistingScenarioFormats`, `BackupScenarios` — i.e. ScenarioStoreSystem's own startup-vs-backup mutual exclusion. |
| `ScenarioDataUpdater.Semaphore[<scenarioName>]` (per-scenario lock) | [Server/System/Scenario/ScenarioBaseDataUpdater.cs:17](../../../Server/System/Scenario/ScenarioBaseDataUpdater.cs#L17) | Each `ScenarioDataUpdater.Write*DataToFile` writer's mutation of the named scenario's `ConfigNode`. |
| `BackupSystem.LockObj` (outer flush lock) | [Server/System/BackupSystem.cs:28](../../../Server/System/BackupSystem.cs#L28) | The whole-universe flush (`VesselStoreSystem.BackupVessels` + `WarpSystem.BackupSubspaces` + `TimeSystem.BackupStartTime` + `ScenarioStoreSystem.BackupScenarios`) vs concurrent flush or archive-snapshot. |

The writer side (e.g. [Server/System/Scenario/ScenarioContractsDataUpdater.cs:31](../../../Server/System/Scenario/ScenarioContractsDataUpdater.cs#L31)) takes `Semaphore["ContractSystem"]` inside a `Task.Run` and freely mutates the `ConfigNode` tree — `contractsNode.AddNode`, `contractsNode.RemoveNode`, `finishedNode.ReplaceNode`.

The backup side ([Server/System/ScenarioStoreSystem.cs:83-93](../../../Server/System/ScenarioStoreSystem.cs#L83-L93)) takes `BackupLock` and calls `scenario.Value.ToString()` on each entry.

**These two locks never intersect.** The writer holds `Semaphore["ContractSystem"]`; the backup holds `BackupLock`. They are operating on the same `ConfigNode` instance from different threads with no mutual exclusion. The `LunaConfigNode.ConfigNode` is **not** thread-safe — it is a wrapper around a list of `CfgNodeValue` with bare-list iteration in `ToString`.

The comment `// ReSharper disable InconsistentlySynchronizedField` at the top of `ScenarioStoreSystem.cs` is the ghost of the same race being half-noticed and explicitly silenced.

### Why `ConcurrentDictionary<string, ConfigNode>` doesn't save us

`CurrentScenarios` is a `ConcurrentDictionary`, which makes `ToArray` / `TryGetValue` / `AddOrUpdate` thread-safe at the dictionary level. That protects against tearing on the dict's own slots, but the *values* are mutable reference types whose internal state has its own concurrency requirements. `AddOrUpdate(key, newNode, (_, _) => newNode)` replaces the slot, but in-place tree mutation on the existing `ConfigNode` (which is what every writer except `RawConfigNodeInsertOrUpdate` does) is invisible to the dict and unprotected.

### Why the existing per-scenario semaphore is the right anchor

`ScenarioDataUpdater.Semaphore` keys a per-scenario `object` lock by scenario name. Every writer in the `Scenario/` folder already takes the matching key before touching its target `ConfigNode`. The fix is therefore: have `BackupScenarios` take the same per-scenario lock around the `ToString()` call. The per-scenario granularity preserves writer parallelism across scenarios (a Funds writer and a Reputation writer don't block each other), and re-entrancy makes the one writer that calls `BackupScenarios` from inside its own lock ([ScenarioPartPurchaseDataUpdater.cs:51](../../../Server/System/Scenario/ScenarioPartPurchaseDataUpdater.cs#L51)) work unchanged.

## Fix design

Three small touches; no wire change, no protocol bump, no new dependencies.

1. **`ScenarioDataUpdater.GetSemaphore(string)`** — new `internal` accessor in [Server/System/Scenario/ScenarioBaseDataUpdater.cs](../../../Server/System/Scenario/ScenarioBaseDataUpdater.cs) that returns `Semaphore.GetOrAdd(scenarioName, _ => new object())`. The semaphore dict stays where it is (alongside the writers); only the accessor is new. Writers don't change — they still take the lock via `lock (Semaphore.GetOrAdd(name, new object()))` exactly as today, which happens to resolve to the same object instance as `GetSemaphore(name)` because `ConcurrentDictionary.GetOrAdd` is idempotent on the key.

2. **`ScenarioStoreSystem.BackupScenarios`** — rewritten to snapshot the dict, then per-scenario take the matching writer lock around just the `ToString()` call, then write to disk *outside* the lock so the disk I/O does not extend the writer-blocking window:

   ```csharp
   public static void BackupScenarios()
   {
       var scenariosInXml = CurrentScenarios.ToArray();
       foreach (var scenario in scenariosInXml)
       {
           string serialized;
           // [fix:BUG-033] Per-scenario lock matches ScenarioDataUpdater writers so
           // ToString does not race with AddNode/RemoveNode/ReplaceNode mutation.
           lock (ScenarioDataUpdater.GetSemaphore(scenario.Key))
           {
               serialized = scenario.Value.ToString();
           }
           FileHandler.WriteToFile(
               Path.Combine(ScenarioSystem.ScenariosPath, $"{scenario.Key}{ScenarioSystem.ScenarioFileFormat}"),
               serialized);
       }
   }
   ```

   The outer `BackupLock` is dropped from this method. It was never load-bearing for this path (it didn't intersect the writer's lock), and dropping it avoids an AB-BA deadlock risk that would have appeared if we tried to take both `BackupLock` and the per-scenario lock together (`ScenarioPartPurchaseDataUpdater` would deadlock against a concurrent `RunBackup` because it holds the per-scenario lock and calls into `BackupScenarios` which would then want `BackupLock` while `RunBackup` already holds `BackupLock` and waits for the per-scenario lock — classic cycle).

   `BackupLock` is preserved on `LoadExistingScenarios` and `ChangeExistingScenarioFormats` for paranoia — both are startup-only paths and their mutual exclusion against each other is cheap to keep.

3. **`Server/ForkBuildInfo.cs`** — append `"BUG-033"` to `ActiveFixes[]` so the boot banner advertises the fix and the runtime log line `[fix:BUG-033]` is grep-discoverable.

### Why not move the semaphore dict into `ScenarioStoreSystem`?

The lock would semantically belong with the data, and SRP says it should live there. But moving it changes ~12 call sites across the `Scenario/` folder and pulls the `ConcurrentDictionary` import along with it. Bigger surgery than this single-bug fix justifies. The accessor pattern is a deliberate compromise — minimal diff, correct behaviour, the lock-ownership weirdness is documented in the comment on `GetSemaphore` for the next person who reads it.

### Why per-scenario locking, not one global ScenarioStore lock

A single global lock for both writers and backup would also fix the race, but at the cost of forcing every writer to serialize against every other writer. Today writers run in parallel across scenarios (a `WriteFundsDataToFile` doesn't block a `WriteAchievementDataToFile`). The per-scenario lock pattern is the existing design; we preserve it.

### Why not snapshot the `ConfigNode` via deep clone

The cleanest "fully immutable snapshot for backup" design would have writers clone the `ConfigNode` after each mutation and atomically swap the dictionary entry. Backup reads the immutable snapshot without any lock. But `LunaConfigNode.ConfigNode` has no `DeepClone()` and writing one is a separate ~50-line addition with its own test surface. Deferred unless this fix turns out to underperform under heavy contention; for the load level of typical LMP servers (4 players, low write rate), the lock-around-`ToString` path is bounded.

## Test plan

`ServerTest/ScenarioStoreBackupRaceTest.cs` (new) — concurrency regression test that demonstrates the fix:

1. Construct a `ConfigNode` populated with enough children that `ToString()` takes a non-trivial number of iterations.
2. Start two threads:
   - Writer: takes `ScenarioDataUpdater.GetSemaphore("TestScen")`, performs `AddNode` / `RemoveNode` on the same `ConfigNode`, releases, repeats.
   - Reader: calls `ScenarioStoreSystem.SerializeUnderWriterLock("TestScen", scenario)` (a new `internal` helper extracted from `BackupScenarios` for direct test access), repeats.
3. Run for ~500ms, then signal stop and `Join` both threads.
4. Assert: no exception escaped either thread.

Without the fix (i.e. if `SerializeUnderWriterLock` skipped the lock and just called `scenario.ToString()`), the test throws within milliseconds. With the fix it runs clean for the full 500ms.

An end-to-end mock-harness test going through the real `ShareProgress*` message handlers + `RunBackup` is possible but expensive (requires temp universe directory setup, message construction, and timing-sensitive race recreation). The pure ServerTest version above proves the lock contract; the mock harness would prove integration. Integration test deferred — not blocking ship.

## Risks and known limitations

1. **Writer-lock window now longer.** Backup acquires the same per-scenario lock writers compete for, so a writer arriving mid-`ToString` waits for the serialization to finish. Worst case: `ContractSystem` scenario has ~hundreds of contracts; `ToString` is ~1ms; a writer arriving during that window waits ≤1ms. Negligible compared to the network RTT that delivered the writer's message in the first place.

2. **BackupScenarios no longer mutually exclusive against `LoadExistingScenarios`.** They could theoretically interleave at startup, but the boot sequence ([MainServer.cs:96](../../../Server/MainServer.cs#L96)) runs `LoadExistingScenarios` *before* `PerformBackupsAsync` ticks for the first time, and the periodic loop gates on `ServerContext.PlayerCount > 0` (no players can be connected during startup because handshake isn't yet up). The race window is theoretical only. If it ever fires, `CurrentScenarios.ToArray()` snapshots a `ConcurrentDictionary` safely and reads fully-built `ConfigNode` instances (`LoadExistingScenarios` builds the node, then `TryAdd`s it — never the other order).

3. **Persisted partial corruption from past crashes.** This fix prevents future corruption but does not heal existing universes that crashed mid-`ToString` and now have a truncated scenario file on disk. Operators on affected servers should restore from a pre-crash archive (Stage 1.1 archive system supports this). The Phase-2 doc for BUG-033 in operator-facing notes should call this out — done in the CLAUDE.md "Stack Notes" update accompanying the fix commit.

4. **`ScenarioPartPurchaseDataUpdater` calling `BackupScenarios` from inside `Semaphore["ResearchAndDevelopment"]`.** Re-entrant lock acquisition is fine on the same thread; verified by inspection that no other writer-thread invokes `BackupScenarios` from inside a per-scenario lock. If a future writer is tempted to add such a call, the new comment on `BackupScenarios` flags the constraint.

## Cross-cutting effects

- `[fix:BUG-033]` is the new log tag; ungrep-spammy because BackupScenarios is a hot path. The fix code does not log per-call — only the once-per-boot banner via `ForkBuildInfo.ActiveFixes`.
- No wire change, no protocol bump.
- No touched code under `LmpClient/`, `LmpCommon/`, or `Lidgren/`. Purely server-side.
- BUG-033 closure leaves BUG-025 and BUG-023 as the remaining top-10 open bugs.
