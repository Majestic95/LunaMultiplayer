# Stage 6 Phase 6.5 — HandleKerbalProto + HandleKerbalRemove per-agency routing — Breakage Analysis

**Branch:** `feature/per-agency`
**Parent commit:** `bbd6ac7e` (Phase 6.4 — HandleKerbalsRequest filter + temporary write-routing boot-refusal)
**Discipline:** Per `[[feedback-breakage-analysis]]`.
**Motivation:** Phase 6.5 closes Stage 6's write-path counterpart to the Phase 6.4 read filter. Per-agency KerbalProto + KerbalRemove handlers route to `Universe/Agencies/{guid:N}/Kerbals/` under gate=on; relay scoped to same-agency clients (effectively owner-only under the current 1:1 OwningPlayerName design — see Risks below); cascade-race guard against `TryDeleteAgency` interleaving; K1 scan skipped under gate=on (structurally moot per spec §Q-K1). The Phase 6.4 temporary boot-refusal `RefuseStartupIfKerbalWriteRoutingNotYetShipped` is REMOVED in this commit, and the two `_UntilPhase65Ships`-suffixed tests + the explicit-refusal test are reverted/deleted.

After Phase 6.5, the combined `AgencySystem.PerAgencyKerbalRosterEnabled` gate is fully functional. Operators can opt in (subject to the Phase 6.2 upgrade-hazard refusal which still gates populated legacy `Universe/Kerbals/`) and writes will land in the correct per-agency subdir.

---

## Scope lock — IS

### 1. `Server/System/FileHandler.cs` — new `WriteAtomic(string path, byte[] data, int numBytes)` overload

Symmetric to `WriteToFile(string, byte[], int)`. Same rotate-and-rename semantics as the existing `WriteAtomic(string, string)` (which writes UTF-8 text via `File.WriteAllText`). Kerbal data is UTF-8 ConfigNode bytes; we could string-convert and reuse the existing overload, but a byte[] overload preserves byte-for-byte parity with the legacy `WriteToFile` path and avoids a round-trip through UTF-8 encoding/decoding for every write.

```csharp
public static void WriteAtomic(string path, byte[] data, int numBytes)
{
    var tmpPath = path + ".tmp";
    var bakPath = path + ".bak";

    lock (GetLockSemaphore(path))
    {
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
                fs.Write(data, 0, numBytes);

            if (File.Exists(path))
            {
                if (File.Exists(bakPath))
                    File.Delete(bakPath);
                File.Move(path, bakPath);
            }

            File.Move(tmpPath, path);
        }
        catch (Exception e)
        {
            LunaLog.Error($"Error writing atomically to file: {path}, Exception: {e}");
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch { }
            }
        }
    }
}
```

### 2. `Server/System/KerbalSystem.cs` — `HandleKerbalProto` per-agency routing

Under `AgencySystem.PerAgencyKerbalRosterEnabled`:
1. Resolve `client.PlayerName → senderAgencyId` via `AgencyByPlayerName`. Miss → log Warning and DROP (per the Phase 6.4 "defensive fallback to legacy" pattern, but for writes we DROP rather than fall back to legacy — under gate=on, legacy is structurally unused and writing there would silently lose data on next operator restart-with-cleared-legacy).
2. Acquire `AgencySystem.GetAgencyLock(senderAgencyId)`.
3. Re-check `AgencySystem.Agencies.ContainsKey(senderAgencyId)` under the lock. Miss → `/deleteagency` cascaded between our resolve and our lock-acquire; log Warning and DROP (the agency no longer exists; writing the file would either land in a partially-deleted subdir or recreate one we're about to delete).
4. Write to `GetKerbalsPathForAgency(senderAgencyId) + {name}.txt` via the new `FileHandler.WriteAtomic(path, bytes, numBytes)` overload.
5. Relay to same-agency clients only — under the current 1:1 OwningPlayerName design this is "no peers, owner-only echo skipped because the sender already has the canonical data" (i.e. zero relay sends). Under any future multi-player-per-agency design, the loop selects the right peers.

Dual-mode silence: under gate=off, fall through to the existing `WriteToFile(KerbalsPath, ...)` + `RelayMessage` path unchanged.

### 3. `Server/System/KerbalSystem.cs` — `HandleKerbalRemove` per-agency routing

Under `AgencySystem.PerAgencyKerbalRosterEnabled`:
1. Resolve `senderAgencyId` via `AgencyByPlayerName`. Miss → Warning and DROP (same rationale as proto).
2. Acquire `GetAgencyLock(senderAgencyId)`.
3. Re-check `Agencies.ContainsKey(senderAgencyId)`. Miss → Warning and DROP.
4. Delete `GetKerbalsPathForAgency(senderAgencyId) + {name}.txt` via `FileHandler.FileDelete`.
5. Relay to same-agency clients only (same as proto).

K1 scan SKIPPED under gate=on. Under gate=off, K1 scan runs unchanged (current behaviour preserved — Stage 5 cohort still uses it).

### 4. `Server/System/Agency/AgencySystem.cs` — REMOVE `RefuseStartupIfKerbalWriteRoutingNotYetShipped`

Phase 6.4 added a temporary predicate that refused boot unconditionally on `PerAgencyKerbalRoster=true`. With Phase 6.5 shipping the write path, the half-shipped hazard is gone — the predicate is removed in this commit along with its two call sites in `LoadExistingAgencies` (the early-return branch + the end-of-method call).

### 5. `ServerTest/PerAgencyKerbalRosterScaffoldingTest.cs` — revert Phase 6.4 test flips

Two tests had their assertions flipped + names suffixed with `_UntilPhase65Ships` in the Phase 6.4 commit:
- `LoadExistingAgencies_RefusesBoot_OnLegacyKerbals_WhenOverrideOn_UntilPhase65Ships` → flip back to `LoadExistingAgencies_AllowsBoot_OnLegacyKerbals_WhenOverrideOn` with `Assert.IsTrue(ServerRunning)`.
- `LoadExistingAgencies_RefusesBoot_WhenLegacyKerbalsDirIsEmpty_UntilPhase65Ships` → flip back to `LoadExistingAgencies_AllowsBoot_WhenLegacyKerbalsDirIsEmpty` with `Assert.IsTrue(ServerRunning)`.

DELETE the Phase-6.4-specific `LoadExistingAgencies_RefusesBoot_OnPerAgencyKerbalRosterTrue_RegardlessOfHazardOverride` test (pins the now-removed temporary predicate).

### 6. `ServerTest/PerAgencyKerbalWriteRoutingTest.cs` — new file

ServerTest cases for `HandleKerbalProto` + `HandleKerbalRemove` per-agency routing. Exercise via direct method calls (no `ClientStructure` shortcut available — but the K1 test pattern in the existing file shows the in-process pattern). Cases:

1. **`HandleKerbalProto_GateOn_WritesToAgencySubdir_NotLegacy`** — register agency, call HandleKerbalProto with a fresh kerbal payload, assert file exists at the per-agency path and NOT at the legacy path.
2. **`HandleKerbalProto_GateOff_WritesToLegacyPath_DualModeSilence`** — PerAgencyKerbalRoster=false; call handler; legacy file exists, no per-agency file.
3. **`HandleKerbalProto_GateOn_AgencyByPlayerNameMiss_DropsWithoutWritingLegacyOrAgency`** — no AgencyByPlayerName entry for sender; assert neither legacy nor any per-agency file is written.
4. **`HandleKerbalProto_GateOn_AgencyDeletedBeforeLockAcquired_DropsWithoutWriting`** — register Alice; simulate cascade by removing from Agencies dictionary AFTER AgencyByPlayerName resolves (in practice TryDeleteAgency removes both atomically, but the cascade-race is between AgencyByPlayerName.TryGetValue and our GetAgencyLock). Easiest reproduction: pre-populate AgencyByPlayerName mapping but not Agencies entry; assert no write happens.
5. **`HandleKerbalRemove_GateOn_DeletesFromAgencySubdir`** — seed file in agency subdir; call HandleKerbalRemove; file gone.
6. **`HandleKerbalRemove_GateOn_K1ScanSkipped`** — pin that under gate=on the K1 scan does NOT run (the kerbal is "aboard another agency's vessel" but the remove still proceeds — under gate=on the wire path doesn't deliver foreign kerbal names to a client, so the K1 check is structurally moot).
7. **`HandleKerbalRemove_GateOff_K1ScanStillEnforced`** — pin that under gate=off the K1 scan still rejects a cross-agency remove (preserves Stage 5.17e-8 behaviour).

Plus 1 new `FileHandler.WriteAtomic(byte[]...)` round-trip test if it doesn't already exist via the existing AgencyState test surface.

### 7. `MockClientTest/PerAgencyKerbalWriteRoutingE2eTest.cs` — new file

Two-client e2e cases (mirrors Phase 6.4 e2e shape):

1. **`TwoClients_GateOn_AlicesKerbalProto_DoesNotLandInBobsAgency_NorRelayToBob`** — Alice + Bob both connect under combined gate; Alice sends a KerbalProto with a unique name "Aurora Test-Kerman"; assert the file appears in Alice's subdir, NOT in Bob's subdir; Bob's subsequent KerbalsRequest does NOT contain Aurora; Bob's inbox does NOT receive an inbound KerbalProto for Aurora.
2. **`TwoClients_GateOff_AlicesKerbalProto_LandsInLegacyAndRelaysToBob`** — same setup but `PerAgencyKerbalRoster=false`; Alice's KerbalProto writes to legacy `Universe/Kerbals/` AND is relayed to Bob's client (preserves the v7 cross-client behaviour). Dual-mode silence proof for the write path.

### 8. `Server/ForkBuildInfo.cs` — update entry

Either replace the Phase 6.4 entry text to encompass 6.4+6.5 combined, OR append a new `"per-agency-kerbal-roster-write-routing"` entry. Going with **append** to keep ForkBuildInfo entries 1:1 with commits — operators reading the boot banner can see both phases distinctly.

---

## Scope lock — IS NOT

- **No wire-shape change.** `KerbalProtoMsgData` + `KerbalRemoveMsgData` schemas unchanged. Agency-id is implicit-from-sender (spec §4).
- **No protocol bump.** Stays at 0.31.0.
- **No K1 grief-guard removal.** Stage 7 territory. Under gate=on the guard is structurally moot but the code stays (low cost; future Stage 7 cleanup removes both this and the client-side `ProtoCrewMember_Die` Harmony patch per spec §Q-K1).
- **No `AgencyState` field change.** Kerbal data lives in per-agency `Kerbals/` subdir on disk only.
- **No client-side change.** Stage 6 client work is Phase 6.6 (tracking-station crew-count UI annotation).
- **No `/setvesselagency` kerbal migration.** Phase 6.8 territory.
- **No Final Frontier integration.** Phase 6.9, optional.

---

## Risk assessment

### Race: `AgencyByPlayerName.TryGetValue` → `GetAgencyLock` → `Agencies.ContainsKey`

The Phase 6.3 race-window note (now codified in CLAUDE.md Stack Notes) identified this exactly. `TryDeleteAgency` cascade holds `GetAgencyLock(agencyId)` throughout the eviction + disk delete. We need to acquire the SAME lock and re-check `Agencies.ContainsKey(senderAgencyId)` AFTER acquisition. Three timeline outcomes:

- **Cascade completes BEFORE our lock acquire:** lock immediately available; `Agencies.ContainsKey` returns false; we DROP with Warning. Correct.
- **Cascade in progress when we try to acquire:** we block until cascade releases; `Agencies.ContainsKey` returns false; we DROP. Correct.
- **Cascade starts AFTER our lock acquire:** cascade blocks; our write completes; cascade then runs the FolderDeleteRecursive on our newly-written file, removing it. The wire-side ack from `MessageQueuer.RelayMessage` is fine — no peers exist in a deleted agency. Outcome: operator-deleted-during-active-mutation behaves as documented (operator's choice; mutation is lost). Acceptable.

### Race: 1:1 OwningPlayerName design assumption

Current design has each agency owning exactly one player (single-string `OwningPlayerName`). Phase 6.5's "relay to same-agency clients" filter selects zero peers under this design — meaning effectively NO relay happens under gate=on (the sender's own client already has the canonical data). If a future Stage-7+ commit introduces multi-player-per-agency, the filter still works correctly (selects only same-agency peers). No design lock-in.

**Operator reconnect-mid-session scenario:** Alice disconnects, reconnects with same PlayerName. Lidgren cleans up the old `ClientStructure` on disconnect (via `ClientConnectionHandler`), so `ServerContext.Clients` only ever has one entry per PlayerName at a time. No relay concern. The new connection re-sends its KerbalsRequest as part of handshake → gets the per-agency roster freshly.

### Cascade race + relay timing

If the cascade runs AFTER our write under the lock but BEFORE our relay completes (relay is a fire-and-forget queue, not blocking on send), the relay still goes out — but to clients of the now-deleted agency, which on the current 1:1 design is zero peers. Relay is structurally a no-op. Even on a future multi-agency-member design, the relay to peers about a now-deleted kerbal is benign (peers' clients will receive a fresh KerbalsRequest on next reconnect).

### Byte handling: `WriteAtomic(byte[], numBytes)` overload

The new byte[] overload mirrors `WriteToFile(string, byte[], int)`'s semantics exactly — `fs.Write(data, 0, numBytes)` not `data.Length`. Caller's `numBytes` may be less than `data.Length` (rented buffer / oversized array). The existing string overload doesn't have this concern because UTF-8 string→bytes is fully owned. Match the byte-overload signature exactly to maintain symmetry with the legacy `WriteToFile` call.

### Disk space / I/O cost: WriteAtomic vs WriteToFile

`WriteAtomic` does THREE filesystem ops per write (tmp create + rotate + rename) vs `WriteToFile`'s one (overwrite). Kerbal writes are chatty (every `onKerbalStatusChange` + `onKerbalLevelUp` + EVA boarding) — at v1 cohort scale (~5 players, ~20 kerbals/agency, ~100 status changes/hour each = ~10k writes/hour). Each kerbal file is ~1-2KB. Tmp+rotate+rename cost is dominated by FS metadata ops, not data write. Acceptable.

### Cross-agency-write rejection — does it apply?

The spec calls for "sender-agency != target-dir-agency" reject. Under our implementation, the target dir IS derived from the sender's agency, so the comparison is tautological — they're always equal by construction. The defensive check therefore reduces to "is the sender's agency in `Agencies` after the lock?" which we already do via the cascade-race guard. No separate cross-agency reject needed; the cascade-race guard subsumes it.

### Empty `AgencyByPlayerName` mapping (gate=on)

Under gate=on, `RegisterAgency` inserts the mapping during the handshake before `HandshakeReply` ships. By the time a client sends KerbalProto/Remove, the mapping MUST exist. The defensive DROP-with-Warning fallback handles a torn-registry edge case that should be unreachable in production but prevents writes from landing in legacy under gate=on.

### Phase 6.4 read-side fallback inconsistency

Phase 6.4's `ResolveKerbalsPathForRequester` falls back to `KerbalsPath` (legacy) when `AgencyByPlayerName` misses. Phase 6.5's write side DROPS instead of falling back to legacy. The asymmetry is deliberate:
- Read fallback to legacy: gives the player SOMETHING (empty if fresh universe, stock roster if upgrade case). Better UX than empty roster.
- Write fallback to legacy under gate=on: would silently land mutations in a directory that's structurally unread under gate=on — silent data loss. DROP is correct.

Documented in the new helper's XML comment.

---

## Test plan

| Surface | Tests | Coverage |
|---|---|---|
| `FileHandler.WriteAtomic(byte[]...)` | 1-2 ServerTest cases (round-trip + numBytes-less-than-array.Length) | New overload |
| `HandleKerbalProto` routing | 4 ServerTest cases (gate-on write / gate-off legacy / mapping-miss DROP / agency-deleted-cascade DROP) | All branches |
| `HandleKerbalRemove` routing | 3 ServerTest cases (gate-on delete / gate-off legacy with K1 enforced / gate-on K1 skipped) | All branches |
| End-to-end | 2 MockClientTest cases (Alice's proto doesn't reach Bob under gate-on / shared-mode parity under gate-off) | Wire round-trip + relay scope |
| Phase 6.4 cleanup | 2 tests flipped back + 1 deleted | Predicate removal verification |

**Expected delta:** ServerTest 701 → ~712 (+9-10). MockClientTest +2.

---

## Rollback

Single-commit; revert restores the Phase 6.4 read-only state. Per-agency disk subdirs from Phase 6.3 remain in place; legacy `Universe/Kerbals/` remains in place. Operators who never opted into `PerAgencyKerbalRoster=true` see zero observable difference. Operators who DID opt in: their writes prior to the revert landed in per-agency subdirs (correct); writes after the revert would land back in legacy under the Phase 6.4 temporary boot-refusal which is also restored by the revert (operators would actually get a Fatal boot refusal preventing the half-shipped state). No persisted-data corruption.

---

## Decisions

- **Write-side DROP on `AgencyByPlayerName` miss, not legacy fallback.** Asymmetric with the read side. Rationale: writes landing in legacy under gate=on are structurally unreadable → silent data loss; reads falling back to legacy give the player a usable roster. Documented in new helper XML.
- **Same-agency-only relay via inline filter, not new MessageQueuer method.** Pattern matches AgencySystemSender's per-target SendToClient loops. Under 1:1 OwningPlayerName this selects zero peers (effectively no relay) but the structure is forward-compatible.
- **New `WriteAtomic(byte[], numBytes)` overload, not string conversion.** Symmetric with `WriteToFile(byte[], numBytes)`; preserves byte-for-byte parity with the legacy write path; avoids round-trip UTF-8 encode/decode for every kerbal write.
- **`Agencies.ContainsKey` under lock as the only cross-agency-write guard.** The "target dir derived from sender agency" structural invariant makes a separate sender-vs-target comparison tautological. The lock-acquired ContainsKey check covers the only race window where they could diverge (cascade between lookup and write).
- **Phase 6.4 temporary refusal REMOVED in this commit.** Two test-name-suffix flips + one explicit-refusal test deletion required.
