# LMP Stability & Shared-Physics Research

## Purpose

This folder contains my research, analysis, and design notes for the Luna Multiplayer fork. The fork's focus is two-fold:

1. Stability and bug fixes against the existing 1.12.x codebase.
2. Improving how the mod handles two (or more) players occupying the same physics scene simultaneously — the long-standing "shared physics bubble" problem in KSP multiplayer.

These notes are for my own use and for anyone who later picks up the work; they are not user-facing documentation. End-user docs live under `Documentation/` (capital D).

## Method

Three phases, each producing committed artifacts in this folder:

- **Phase 1 — Inventory.** Survey of the most-reported bugs and pain points from public sources: the LMP issue tracker (open + closed), KSP forums, Reddit, and any developer/community write-ups. For each item: a one-paragraph description, links to primary sources, evidence of frequency or severity, and the rough subsystem(s) implicated. Output: `01-bug-inventory.md`.

- **Phase 2 — Analysis.** For each top-tier bug from Phase 1, a static read of the relevant code, plus a check of recent upstream activity touching the same area (so I do not duplicate work in flight). Output: one file per bug under `02-analysis/`.

- **Phase 3 — Designs.** For each bug I decide to tackle, a design document covering root-cause hypothesis, proposed fix, alternatives considered, expected risks, and the specific files and tests involved. Output: one file per design under `03-designs/`.

Phase 3 stops at design — no code changes are written as part of this research stream. Implementation decisions are made later, one design at a time.

## Coordination

Upstream `LunaMultiplayer/LunaMultiplayer` is being actively maintained again as of April 2026 after a multi-year quiet period. Before starting any code work in an area, I check upstream for recent commits and open PRs in that subsystem to avoid duplicated effort.

The fork's `upstream` git remote points at the canonical repo; `git fetch upstream` is the first step of any new analysis session.

## Layout

```
docs/research/
  00-overview.md         this file
  01-bug-inventory.md    Phase 1 output
  02-analysis/           Phase 2, one file per bug
  03-designs/            Phase 3, one file per chosen fix
```
