# Phase-2 analyses — code-validated bug write-ups

One file per bug. Each Phase-2 doc:
- Quotes the actual code locations and line numbers as of the validation commit.
- Records the diagnosed root cause with evidence pointers.
- Recommends a fix scope and rejects alternatives with reasoning.
- Lists dependencies, risks, and open questions to resolve in Phase-3 design.

The cross-bug synthesis that motivated the first batch lives in [03-time-sync-fix-brainstorm.md](../03-time-sync-fix-brainstorm.md) (genesis artifact — pre-validation).

## First batch — time / subspace correctness

Validated against `master` at commit `48df64bd` (2026-05-16). Ordered by Option C implementation sequence (shippable wins first, heaviest architectural change last).

| # | File | Inventory IDs | Scope | Why this slot |
|---|------|---------------|-------|---------------|
| 1 | [bug-051-stuck-warp-limbo.md](bug-051-stuck-warp-limbo.md) (5a section) | BUG-051a | Server-side request dedup with optional `RequestSeq`. ~50 LOC. No proto break. | Smallest robustness baseline. Sets the seq-number pattern before any retry-adjacent work. |
| 2 | [bug-001-solo-subspace-catchup.md](bug-001-solo-subspace-catchup.md) | BUG-001 | Server-authoritative solo-subspace tracking + `SoloSubspaceAdvance` message. ~100 LOC. | First visible-to-players win. CLAUDE.md cursor. Additive message, no proto break. |
| 3 | [bug-003-004-frozen-vessel-interp-cap.md](bug-003-004-frozen-vessel-interp-cap.md) | BUG-003 + BUG-004 | Symmetric (or finite-asymmetric) `MaxInterpolationDuration` cap. One-line change. | Smallest possible diff. Pure client-side. Highest critic confidence. |
| 4 | [bug-051-stuck-warp-limbo.md](bug-051-stuck-warp-limbo.md) (5b section) | BUG-051b | Client steady-state predicate (500ms retry while stuck-at-warp). ~30 LOC. | Pairs with 5a. Depends on 5a's dedup safety net. |
| 5 | [bug-014-extensionmethods-rb-audit.md](bug-014-extensionmethods-rb-audit.md) | BUG-014 remainder | Continue PR #628's pattern across remaining `transform.*`-only setters in `ExtensionMethods/`. Multiple small atomic units. | Small, isolated, interleavable. |
| 6 | [bug-005-006-cross-subspace-lock.md](bug-005-006-cross-subspace-lock.md) | BUG-005 + BUG-006 | Add `AuthoritativeSubspaceId` per vessel; rekey lock registry; restore the `fbc7a8c`-disabled broadcasts; bump `LMP_PROTOCOL_VERSION`. | Heaviest. Capstone. Protocol break OK on fork. |

## Naming convention

`bug-NNN-short-name.md` where `NNN` is the BUG-id from [`01-bug-inventory.md`](../01-bug-inventory.md). When one fix addresses multiple bugs, the filename combines the IDs (`bug-003-004-...`).

## Status field

Each doc carries one of:
- **Validated against `master` at commit `<sha>`** — line numbers + diagnoses verified by direct code read.
- **PRE-VALIDATION** — diagnosis copied from brainstorm; code-walk not yet done. Treat as a starting hypothesis.

## Phase-3 designs

Once a Phase-2 doc is ready to become work, a Phase-3 design doc lands at `docs/research/03-designs/<bug-id>-design.md` (folder still empty as of `48df64bd`).
