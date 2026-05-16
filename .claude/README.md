# Luna Multiplayer — Claude Code Configuration

This folder is **fork-local config** for the `Majestic95/LunaMultiplayer` fork. It is **not** meant to be PR'd to upstream `LunaMultiplayer/LunaMultiplayer` — upstream has historical sensitivity to AI-assisted contributions (see issue #588 / Fierce-Cat incident), so we keep this scaffolding strictly on our fork.

When opening an upstream PR: use `git add` with explicit paths, **never** `git add -A`, and do not push `.claude/` or `CLAUDE.md` to upstream.

---

## Hooks (Automated)

Two hooks run automatically every time a file is edited or written:

### 1. File Size Checker (`hooks/check-file-size.sh`)
- Warns at **600 lines** (soft cap)
- Errors at **900 lines** (hard cap)
- Targets `.cs` files only
- Skips: `*Test.cs`, files under `/Test/`, `/ServerTest/`, `/LmpCommonTest/`, `/LmpMasterServerTest/`, `Properties/AssemblyInfo.cs`, `.Designer.cs`, `.g.cs`, `/bin/`, `/obj/`

Caps are higher than CE's 400/500 because C# is more verbose than TypeScript. Existing 600-line files (e.g., `Json.cs` at 635, `ShareContractsEvents.cs` at 611, `MainSystem.cs` at 579) trigger soft-cap notes only — splits are aspirational, not mandatory for legacy code. `ScenarioSystem.cs` (802 lines) lives in the warn-but-tolerate band; only true 900+ outliers must split before merge.

### 2. Change Classifier (`hooks/classify-change.sh`)
- Detects what kind of code was changed based on file path
- Suggests which specialized review agent to run
- Output format: `REVIEW_SUGGESTED: [Agent Name] — file. Run: review-agents/<file>.md`

#### Classification Rules

| File Path Contains | Domain | Suggested Agent |
|-------------------|--------|-----------------|
| `/Server/Message/`, `/Lidgren/`, `/LmpCommon/Message/` | Networking | `network-review.md` |
| `/Server/System/*Backup*`, `FileHandler.cs`, `Universe.cs` | Persistence | `persistence-review.md` |
| `/Server/System/`, `/Server/Command/`, `/Server/Web/`, `/Server/...` | Server systems | `server-systems-review.md` |
| `/LmpClient/Harmony/` | Harmony patches | `client-harmony-review.md` |
| `/LmpClient/Systems/`, other `/LmpClient/` | Client (mod side) | `client-harmony-review.md` |
| `/LmpCommon/` (non-Message) | Shared protocol | `network-review.md` |
| `CLAUDE.md`, `/docs/`, `/Documentation/` | Architecture | `architecture-review.md` |
| Everything else | General | `review-prompt.md` |

### Stop Hook
At session end, prints a reminder to update `CLAUDE.md` if conventions or systems changed, and a reminder **not** to push to upstream.

### Hook Implementation Note
Hooks parse the tool-input JSON with `python` (not `jq`), because `jq` is not present in the user's Windows git-bash environment — using `jq` causes hooks to silently no-op on Windows (a known issue elsewhere). Both scripts assume Python 3 on PATH (Python 3.13 confirmed working).

---

## Review Agents (Manual)

Five focused review agents live in `review-agents/`. Each has a domain-specific checklist and runs via `claude --print`.

### How to Use (Second Terminal)

Review the last commit through a focused agent:
```bash
cd F:\luna-multiplayer
claude --print "$(cat .claude/review-agents/server-systems-review.md)\n\nReview this diff:\n$(git diff HEAD~1)"
```

Or review uncommitted work:
```bash
claude --print "$(cat .claude/review-agents/network-review.md)\n\nReview this diff:\n$(git diff)"
```

### Available Agents

#### Networking (`review-agents/network-review.md`)
- Lidgren transport, message handlers, `LmpCommon` wire contract
- Client-input validation, broadcast safety, protocol versioning
- AdmiralRadish coordination on docking / coupling / scenario sync / lock handoff

#### Server Systems (`review-agents/server-systems-review.md`)
- `Server/System/*`, `Server/Command/*`, `Server/Settings/*`, `Server/Web/*`
- Singleton awareness, `Share*` system pattern, admin command safety
- `LunaLog` discipline, `net10.0` target constraints

#### Client / Harmony (`review-agents/client-harmony-review.md`)
- `LmpClient/Systems/`, `LmpClient/Harmony/`, `LmpClient/VesselUtilities/`
- .NET Framework 4.7.2 / Mono runtime constraints
- Unity main-thread discipline, Harmony patch hygiene

#### Persistence (`review-agents/persistence-review.md`)
- `BackupSystem`, `FileHandler`, `Universe.cs`, archive lifecycle
- `RunBackup` (flush) vs `RunArchiveBackup` (snapshot) distinction
- Atomic writes, retention, restore safety

#### Architecture (`review-agents/architecture-review.md`)
- `CLAUDE.md`, `docs/`, csproj structure, design decisions
- Stage discipline (Stage 1–4 on `master`, Stage 5 on `feature/per-agency`)
- Dependency direction (Server / LmpClient / LmpCommon)

### Issue Severity

All agents use the same scale:
- **[MUST FIX]** — violates a hard rule, will cause problems
- **[SHOULD FIX]** — violates a soft rule, code quality concern
- **[CONSIDER]** — suggestion for improvement, non-blocking

---

## Session End Reminder

The `Stop` hook reminds you to update `CLAUDE.md` when systems or conventions change, and to push to `origin` (Majestic95 fork) only — **never** `upstream`.
