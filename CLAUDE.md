# Luna Multiplayer

## Project Overview

**Luna Multiplayer (LMP)** is a multiplayer mod for Kerbal Space Program 1. Players share a persistent universe, sync vessels in flight, coordinate docking, and share career-mode resources (funds, science, contracts) across one server. This repository is a personal fork of [LunaMultiplayer/LunaMultiplayer](https://github.com/LunaMultiplayer/LunaMultiplayer) (upstream). Our remote `origin` is [Majestic95/LunaMultiplayer](https://github.com/Majestic95/LunaMultiplayer).

- **Goals on this fork:** stability fixes, shared-physics improvements, eventual per-agency career progression (Stage 5).
- **Coordination:** upstream is actively maintained again by `AdmiralRadish` as of April 2026; docking, vessel coupling, scenario sync, and lock handoff are their active turf.
- **Prior art for per-agency:** [PlagueNZ/LunaMultiplayer-SplitProgression](https://github.com/PlagueNZ/LunaMultiplayer-SplitProgression) — used as **benchmark only**, not source. Our per-agency work goes ground-up on `feature/per-agency`.

---

## Tech Stack & Build Targets

| Project | Target | Runtime | Notes |
|---------|--------|---------|-------|
| `Server` | `net10.0` | .NET 10 | Dedicated headless server. Authoritative for all game state. |
| `LmpClient` | `net472` | Mono (KSP's bundled runtime) | KSP plugin DLL, loaded into KSP1. Builds locally with the .NET 4.7.2 Developer Pack + KSP DLLs in `External/KSPLibraries/` — see "Build & Run → LmpClient". |
| `LmpCommon` | `netstandard2.0` + `net10.0` + `net472` | Multi-target | Wire protocol, message types, shared utilities. |
| `LmpMasterServer` | `net10.0` | .NET 10 | Optional public master-server registry. |
| `LmpUpdater` | `net10.0` | .NET 10 | Self-update tool. |
| `Lidgren.*`, `LmpCommon`-vendored Lidgren | Multi-target | — | Low-level UDP transport (forked from `lidgren-network-gen3`). |

**SDK pin (`global.json`):** `10.0.100` with `rollForward: latestFeature`. The system `dotnet` on Windows is typically 7.x and cannot satisfy this. Use the user-installed `.NET 10 SDK` at `C:\Users\austi\.dotnet\` (installed without admin, fully reversible).

---

## Repository Structure

```
luna-multiplayer/
├── CLAUDE.md                   # This file — project bible
├── .claude/                    # Fork-local Claude Code config (hooks, review agents) — NOT for upstream
├── README.md                   # Public project README (upstream-inherited)
├── LunaMultiPlayer.sln
├── global.json                 # SDK pin (10.0.100, rollForward: latestFeature)
├── docker-compose.yml          # Dev compose (server + master server)
├── Dockerfile_Server / Dockerfile_MasterServer
├── Server/                     # Headless dedicated server (net10.0)
│   ├── Server.csproj
│   ├── MainServer.cs           # Entry point + task wiring (backups, log thread, command thread, network)
│   ├── Context/
│   │   ├── ServerContext.cs    # Server-wide singletons (clock, universe path, run flag)
│   │   └── Universe.cs         # Universe directory / persistent-state root
│   ├── System/                 # Server-side game systems
│   │   ├── BackupSystem.cs     # RunBackup() = flush; RunArchiveBackup() = snapshot. DO NOT CONFLATE.
│   │   ├── FileHandler.cs      # Canonical disk-IO wrapper. Use this, not raw File.*.
│   │   ├── Share*System.cs     # 13 Share systems broadcasting career mutations (funds, science, contracts, ...)
│   │   ├── LockSystem.cs       # Authoritative lock state (vessel control, scenario, asteroid)
│   │   ├── HandshakeSystem.cs  # Initial connection + version negotiation
│   │   ├── VesselSystem*.cs    # (in Server/Message/) Vessel proto/position/flight-state ingest
│   │   ├── ScenarioSystem.cs   # Career scenario aggregation
│   │   ├── CraftLibrarySystem.cs
│   │   ├── ScreenshotSystem.cs
│   │   └── ...
│   ├── Message/                # Lidgren message handlers (Reader/* validates, Server/* dispatches)
│   ├── Command/
│   │   ├── CommandHandler.cs
│   │   └── Command/            # 18 admin commands: backup, ban, kick, nuke, set-funds, set-science, ...
│   ├── Settings/
│   │   ├── SettingsHandler.cs
│   │   ├── Base/
│   │   ├── Structures/         # *SettingsStore static wrappers
│   │   └── Definition/         # 12 XML-serialized settings groups (Gameplay, Connection, Log, ...)
│   ├── Log/
│   │   ├── LunaLog.cs          # Server logger (extends LmpCommon BaseLogger; adds file append)
│   │   ├── LogThread.cs        # Async rollover (daily) + expire pass
│   │   └── LogExpire.cs        # Deletes logs older than LogSettings.ExpireLogs days
│   ├── Web/
│   │   ├── WebServer.cs        # uhttpsharp-based HTTP endpoint (port 8900) — admin dashboard surface
│   │   ├── Handlers/           # ServerInformationRestController + JsonGetHandler (Stage 3.7) + ExceptionHandler
│   │   └── Structures/         # ServerInformation, ForkInformation (Stage 3.7), LogSnapshot (Stage 3.7)
│   ├── Client/                 # Connected-client state and lifecycle
│   ├── Upnp/                   # Optional UPnP port mapping
│   └── Plugin/                 # Server-side mod loader (LMPModInterface)
├── LmpClient/                  # KSP plugin (net472, Mono)
│   ├── Systems/                # 50+ client systems mirroring Server/System (vessel sync, share*, locks, ...)
│   ├── Harmony/                # 30+ Harmony patches over KSP internals
│   ├── Base/                   # Base classes (System<T>, Sender<T>, StyleLibrary)
│   ├── Network/                # Lidgren client transport wrapper
│   ├── VesselUtilities/        # Docking, EVA, part-attach helpers
│   ├── ModuleStore/            # PartModule field interception + patching
│   ├── Windows/                # IMGUI windows (server list, status, chat, locks)
│   ├── MainSystem.cs           # Update loop + dispatch into systems
│   └── Utilities/Json.cs       # Vendored Newtonsoft adapter
├── LmpCommon/                  # Shared wire / utils (multi-target)
│   ├── Message/                # Message types (request/reply/data) — wire contract
│   ├── ModFile/                # Mod-control file parsing
│   ├── Time/                   # LunaNetworkTime (UTC clock used by both sides)
│   ├── BaseLogger.cs           # Shared logger base (server's LunaLog extends this)
│   └── Locks/                  # Lock primitives shared with server
├── LmpMasterServer/            # Optional public registry
├── LmpUpdater/                 # Self-updater
├── Lidgren/ Lidgren.Core/ Lidgren.Net/   # Vendored UDP transport
├── ServerTest/                 # NUnit tests for server (18 tests)
├── LmpCommonTest/              # NUnit tests for LmpCommon
├── LmpClientTest/              # MSTest on net472 — client-internal unit tests
├── docs/
│   └── research/
│       ├── 00-overview.md      # Inventory method
│       ├── 01-bug-inventory.md # 50 bugs catalogued by subsystem
│       └── 02-analysis/        # Phase-2 analyses (per-bug deep dives) — populated as bugs are picked up
└── Documentation/              # Upstream-inherited end-user docs
```

---

## Build & Run

### Server (the workstream this fork focuses on)

Use the user-installed .NET 10 SDK, **not** the system `dotnet`:

```bash
"/c/Users/austi/.dotnet/dotnet.exe" build Server/Server.csproj -c Release
"/c/Users/austi/.dotnet/dotnet.exe" run --project Server/Server.csproj -c Release
```

Tests:

```bash
"/c/Users/austi/.dotnet/dotnet.exe" test ServerTest/ServerTest.csproj -c Release
"/c/Users/austi/.dotnet/dotnet.exe" test LmpCommonTest/LmpCommonTest.csproj -c Release
```

### LmpClient (KSP plugin)

Targets `net472` (Mono runtime — KSP's bundled CLR). Builds locally via the same user-installed `.NET 10 SDK` once two prerequisites are in place:

1. **.NET Framework 4.7.2 Developer Pack** — `NDP472-DevPack-ENU.exe` from https://aka.ms/msbuild/developerpacks. Installs the v4.7.2 targeting pack under `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\`. `LmpClient/Directory.Build.props` then wires `TargetFrameworkRootPath` so the SDK MSBuild finds it (the SDK's default resolution is unreliable on some .NET 10 SDK builds — passing this explicitly is the workaround).
2. **KSP/Unity DLLs in `External/KSPLibraries/`** — `Assembly-CSharp.dll`, `System.dll`, `System.Xml.dll`, plus 10 `UnityEngine.*.dll` modules (see `LmpClient.csproj` `<HintPath>` entries for the full list). Two sources:
   - Extract `External/KSPLibraries/KSPLibraries.7z` — but it's password-encrypted and the password lives in the upstream's AppVeyor secret (rotated 2017 commit `5f7a0026`, never made public). Upstream's current GitHub Actions workflow doesn't build LmpClient either.
   - **Copy from a local KSP install** (preferred — legally cleaner): `<KSP>/KSP_x64_Data/Managed/<dll>`. The 13 files needed total ~18 MB.

Build:
```bash
"/c/Users/austi/.dotnet/dotnet.exe" build LmpClient/LmpClient.csproj -c Release
```

Outputs `LmpClient/bin/Release/LmpClient.dll` (~3 MB) along with vendored deps. Pre-existing warnings on this project: 4 × MSB3245 (missing `System.Buffers`/`System.Memory`/`System.Numerics.Vectors`/`System.Runtime.CompilerServices.Unsafe` HintPath to a non-existent `External/Nuget/` folder — supplied by KSP's Mono at load time, not a real problem); 2 × NU1701 (CachedQuickLz package compat); 1 × CS0169 (unused field in `Windows/Mod/ModWindow.cs`). These are noise; do not "fix" them in unrelated diffs.

For end-user deployment, the DLL is copied to KSP's `GameData/LunaMultiPlayer/Plugins/` per the existing `Scripts/CopyToKSPDirectory.bat`.

### Pre-existing build warnings (do not "fix as you go")

30 warnings exist on `master`:
- `CA1416` platform warnings in `ScreenshotSystem`
- `CS0114` in `Lidgren/NetRandom.cs`
- `NU1701` in `CachedQuickLz` + `LunaConfigNode` (packaged for older TFM)

These are pre-existing noise. They belong in a deliberate cleanup pass, **not** in unrelated diffs.

---

## Coding Conventions

> **Mandatory breakage analysis** before non-trivial changes: scope, edge cases, test plan. The full discipline lives in user-memory `feedback_breakage_analysis.md`.

### C#
- **Naming:** `PascalCase` for types and public members, `camelCase` for parameters/locals, `_camelCase` for private fields. File name matches the type name (one class per file).
- **No `Console.WriteLine`.** Use `LunaLog.Normal` (general info), `Info` (white text), `Warning`, `Error`, `Fatal`, `Debug`, `NetworkDebug`, `NetworkVerboseDebug`, `ChatMessage`. The project convention is that **`LunaLog.Normal` is the user-visible info level** (existing `BackupCommand`-style usage), even though `Info` also exists.
- **No `File.*` for Universe state.** Use `FileHandler`. It canonicalizes paths, locks per file, and centralizes error handling.
- **Catch blocks must log via `LunaLog.Error` / `Fatal` or rethrow.** No silent swallowing.
- **No `record`, `init`, `Span<T>`, `Index/Range`, top-level statements, or `System.Text.Json` in `LmpClient`** — it targets `net472` / Mono.

### Enforced rules
- **File-size caps:** soft 600 / hard 900 lines for `.cs` (test files exempt). C# is more verbose than TypeScript, so caps are higher than CE's 400/500. Documented exceptions are allowed via a top-of-file comment explaining why splitting hurts more than it helps. The `.claude/hooks/check-file-size.sh` hook surfaces overshoots; treat the warning as a backlog signal.
- **Every server system needs unit tests** as it solidifies. `ServerTest/` currently covers 6 of the ~20 server systems — new behaviour adds tests rather than borrowing from the existing gap.
- **Update CLAUDE.md** when a system, command, setting, or convention changes.

---

## Git Conventions

### Branching
- `master` — tracks `Majestic95/LunaMultiplayer` (our fork's main). Tracks upstream commits we've pulled.
- `feature/per-agency` — Stage 5 work, **not yet created**. Per-agency career goes here ground-up.
- `fix/<topic>` — short-lived branches off `master` for upstream PRs.

### Pushing
- **Push to `origin` only** (`Majestic95/LunaMultiplayer`). **Never to `upstream`** (`LunaMultiplayer/LunaMultiplayer`).
- When opening an upstream PR (from `fix/<topic>` → `upstream/master`), use explicit `git add <paths>` — **never `git add -A`** — and do not push `.claude/` or `CLAUDE.md`. They are fork-local.

### Commit messages
Conventional Commits:
```
<type>(<scope>): <short description>
```
- **Types:** `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `perf`, `style`
- **Scopes:** `server`, `client`, `common`, `network`, `vessel`, `lock`, `share`, `scenario`, `harmony`, `backup`, `command`, `settings`, `log`, `web`, `docs`

### No AI attribution
**Silent partner rule applies to everything**: commits, PRs, issues, code comments, committed docs.
- Strip `Co-Authored-By: Claude` from commit trailers.
- Strip "Generated with Claude Code" footers from PR descriptions.
- The upstream LMP community has reverted AI-attributed contributions before (Fierce-Cat / issue #588). On *this fork* we are simply discreet; on *upstream PRs* AI attribution is a likely rejection vector.

### PR Checklist
- [ ] `dotnet build -c Release` clean (no NEW warnings)
- [ ] `ServerTest` + `LmpCommonTest` pass
- [ ] No file >900 lines (or top-of-file comment explaining the exception)
- [ ] CLAUDE.md updated if systems / commands / settings / conventions changed
- [ ] Commit subject follows Conventional Commits with an allowed scope
- [ ] No AI attribution in commit / PR / comments
- [ ] If touching docking / coupling / scenario sync / lock handoff: confirmed against `upstream/master` (AdmiralRadish-owned turf)

---

## Architecture Principles

1. **Server-authoritative.** All game state (vessel positions, locks, career resources, scenario data) is owned by the server. Clients send intents / observations; server validates and broadcasts.
2. **Shared-agency career, today.** LMP is shared-agency by design — `Funding.Instance`, `ResearchAndDevelopment.Instance`, etc. are game-wide singletons. 13 `Share*Sender` systems propagate mutations to all clients. **Per-agency** (Stage 5) lives on its own branch and is a wide-but-shallow rewrite — not architectural impossibility.
3. **Lidgren UDP, custom message protocol.** No HTTP for gameplay. Wire schemas live in `LmpCommon/Message/`. Backward compatibility is enforced via `HandshakeSystem`.
4. **System pattern.** Each subsystem is its own class in `Server/System/` (or `LmpClient/Systems/`). Senders broadcast outbound messages, readers ingest inbound. Inter-system coupling stays explicit.
5. **Harmony patches are surgical.** Each patch in `LmpClient/Harmony/` targets one KSP type. Used to intercept singleton mutations (precedent: `ContractPreLoader_Filter`).
6. **`FileHandler` is the disk gateway.** Universe state never bypasses it.
7. **Backup as a first-class operation.** `RunBackup` (flush in-memory to canonical files) and `RunArchiveBackup` (timestamped snapshot with retention + restore) are distinct.

---

## Server System Inventory

Server-side systems (selected, see `Server/System/` for the full list):

| System | File | Purpose |
|--------|------|---------|
| `BackupSystem` | `BackupSystem.cs` | Flush + timestamped archive snapshots + restore |
| `FileHandler` | `FileHandler.cs` | Canonical disk-IO wrapper |
| `HandshakeSystem` | `HandshakeSystem.cs` | Initial connect, version negotiation, identity |
| `LockSystem` | `LockSystem.cs` | Authoritative lock state |
| `Share*System` (×13) | `Share*.cs` | Career-mode broadcast: funds, science, contracts, reputation, achievements, tech, etc. |
| `ScenarioSystem` | `ScenarioSystem.cs` | Career scenario aggregation |
| `GroupSystem` | `GroupSystem.cs` | Player groups (scaffolding only — name + members, no resource fields) |
| `CraftLibrarySystem` | `CraftLibrarySystem.cs` | Craft sharing |
| `ScreenshotSystem` | `ScreenshotSystem.cs` | Screenshot ingest + distribution |
| `KerbalSystem` | `KerbalSystem.cs` | Kerbal roster sync |
| `WarpSystem` | `WarpSystem.cs` | Time-warp coordination |
| `FlagSystem` | `FlagSystem.cs` | Flag asset distribution |
| `GcSystem` | `GcSystem.cs` | Periodic forced GC pass |
| `ModFileSystem` | `ModFileSystem.cs` | Mod-control file enforcement |

Vessel-side ingest lives under `Server/Message/` (proto, position, flight state, part sync, fairings, action groups, eva, decouple/couple). 30+ Harmony patches on the client mirror these.

## Web Dashboard (`Server/Web/`)

uhttpsharp HTTP server on port 8900 (configurable via `WebsiteSettings.Port`). Read-only JSON endpoints — there is no admin write path yet. Disabled by setting `WebsiteSettings.EnableWebsite = false`.

| Route | Content-Type | Payload | Refresh |
|-------|--------------|---------|---------|
| `GET /` | `application/json` | `ServerInformation` — current players, vessels, subspaces, settings | Polled every `WebsiteSettings.RefreshIntervalMs` (default 5000ms) by `RefreshWebServerInformationAsync` |
| `GET /fork` | `application/json` | `ForkInformation` — `ForkName` + `ProtocolVersion` + `ActiveFixes[]` (Stage 3.7) | Static — built per request from `ForkBuildInfo` |
| `GET /log` | `text/plain` | Human-readable log dump: `# fork / # protocol / # fixes / # ring` header followed by `LogRingBuffer` entries oldest-first. Operator-friendly in a browser tab. Formatter at `Server/Web/Formatting/LogTextFormatter.cs`. | Snapshot per request |
| `GET /logjson` | `application/json` | `LogSnapshot` — `Capacity` + `Count` + `Entries[]` for tooling that wants the structured fields | Snapshot per request |

Add a new JSON endpoint by registering one more `.With("route", new JsonGetHandler(() => new Payload()))` chain in `WebServer.StartWebServer`. For plain-text endpoints use `TextGetHandler` with a `Func<string>` factory. Prefer either over the `RestHandler<T>` + `IRestController<T>` pair for read-only routes — the REST pair is five per-endpoint methods, mostly `MethodNotAllowed` boilerplate.

## Settings (`Server/Settings/Definition/`)

12 XML-serialized settings groups:
- `GeneralSettings`, `ConnectionSettings`, `GameplaySettings`, `CraftSettings`, `DebugSettings`, `DedicatedServerSettings`
- `IntervalSettings`, `LogSettings`, `ScreenshotSettings`, `WarpSettings`, `WebsiteSettings`, `MasterServerSettings`

New settings: add a field with `[XmlElement]` and a default value, then verify round-trip via `SettingsHandler.LoadSettings` / `SaveSettings`.

## Admin Commands (`Server/Command/Command/`)

18 commands: `backup`, `restoreBackup`, `ban`, `kick`, `say`, `nuke`, `dekessler`, `clearvessels`, `vessel`, `cleancontracts`, `setfunds`, `setscience`, `changesettings`, `countclients`, `listclients`, `listlocks`, `connectionstats`, `displayhelp`, `restartserver`.

Mutating commands should default to non-destructive (precedent: `BackupCommand` requires explicit subcommands).

---

## Test Suite

`ServerTest/` (83 tests on `net10.0` via MSTest):
- `FileHandlerTest` — disk-IO round-trip
- `HandshakeSystemValidatorTest` — handshake validation
- `LockSystemTest` — lock state transitions + cross-subspace acquire rejection (BUG-005/006)
- `LunaMathTest` — math utilities
- `VesselStoreSystemTest` — vessel store invariants
- `VesselTest` — vessel object behavior
- `LogTest` — `LogEntry.Parse` + `LogRingBuffer` (Stage 1.2)
- `WarpRequestCacheTest` — `(player, seq)` dedup cache (BUG-051a)
- `WarpSoloDetectionTest` — solo-subspace transition logic (BUG-001)
- `VesselAuthorityTest` — `AuthoritativeSubspaceId` round-trip, `IsStrictlyPast`, `RemoveSubspace` vessel-auth guard (BUG-005/006)
- `WebDashboardTest` — `ForkInformation` + `LogSnapshot` payloads (Stage 3.7)
- `VesselSanitizerTest` — `ModuleReactionWheel` `stateString` locale-normalisation (BUG-013, session 5)

`LmpCommonTest/`:
- `LunaNetUtilsTest`, `MessageStoreTest`, `SerializationTests`, `TimeTests`

`LmpClientTest/` (21 tests on `net472` via MSTest, Stage 4.10 + BUG-008 Phase A):
- `CommonUtilTest` — pure-BCL proof tests over `LmpClient.Utilities.CommonUtil` (`ScrambledEquals` + `CombinePaths`). The point is to demonstrate the scaffold works: SDK-style `net472` test project, references the old-style `LmpClient.csproj`, runs under `dotnet test` from the user-installed .NET 10 SDK.
- `PqsAlignmentDecisionTest` — exhaustive coverage of the pure decision math in `PqsAlignmentRoutine.NeedsRealignment` and `IsStable` (BUG-008 Phase A). The KSP-bound coroutine that drives polling and snaps the vessel pose cannot be unit-tested (no PQS at test time); the named-constant pins (`DefaultThresholdMeters`, `DefaultStabilityDeltaMeters`, `MaxPollSeconds`) guard against a careless edit silently changing cadence. Future client-internal regression tests (BUG-003/004 interp cap math, BUG-051b retry predicate) join this project as their helpers get extracted into testable forms.

`MockClientTest/` (7 tests on `net10.0` via MSTest, Stage 4.9 v1 + 4.10):
- `HandshakeSmokeTest` — proves the in-process harness wires up: `ServerHarness.Start` brings up a real Server on a free localhost port, `MockNetClient` connects via Lidgren and completes the LMP handshake, server registers the `ClientStructure`. Design + future work in `docs/research/04-mock-client-harness-design.md`.
- `Bug051aDedupTest` — end-to-end BUG-051a coverage: duplicate `WarpNewSubspaceMsgData` with same `RequestSeq` returns the same subspace (no orphan); `RequestSeq=0` sentinel always mints (pre-fix client backward-compat).
- `Bug001SoloBroadcastTest` — end-to-end BUG-001 coverage: `WarpSystem.RefreshSoloStatuses` flips a subspace's `Solo` flag and broadcasts `WarpSubspaceSoloStatusMsgData` to a connected mock client when occupancy transitions in either direction (1 occupant → solo, 2 occupants → non-solo). Drives `RefreshSoloStatuses` directly to avoid waiting on the periodic-task interval — per-subspace correctness is already covered by `WarpSoloDetectionTest`.
- `Bug005SubspaceRejectTest` — end-to-end BUG-005/006 coverage: two mock clients in different subspaces, a planted vessel in `VesselStoreSystem.CurrentVessels`. Past-subspace client → future-auth vessel proto-update is rejected (stored vessel reference unchanged, no relay to the future client); future-subspace client → past-auth vessel is accepted (relay reaches the past client). Sample vessel ConfigNode is loaded from `ServerTest/XmlExampleFiles/Others/` via a relative-path walk-up — no fixture duplication.

Gaps to close as we touch each subsystem: backup archive lifecycle, settings round-trip, `Share*` broadcast routing. Client-side regression coverage for BUG-003/004 (interp cap math) and BUG-051b (retry predicate) now has a home in `LmpClientTest/` — those tests still need the relevant helpers to be extracted from their current inline call sites before they can be authored. BUG-008 Phase A decision math is already covered by `PqsAlignmentDecisionTest`; the KSP-bound coroutine has to be soak-validated in-game.

---

## Key Design Decisions

| Decision | Choice | Reasoning |
|----------|--------|-----------|
| Transport | Lidgren UDP, custom messages | KSP needs low-latency vessel sync; HTTP/WS doesn't fit the cadence |
| Server framework | `net10.0` (was net5/Core in upstream) | Modern .NET, async/await ergonomics, current LTS-ish |
| Client framework | .NET Framework 4.7.2 | Required by KSP's Mono runtime; cannot upgrade without KSP itself changing |
| Career model | Shared-agency by default; per-agency on `feature/per-agency` | Original LMP design; per-agency is opt-in to avoid forking the community |
| Logging | `LunaLog` extends `BaseLogger` (LmpCommon) | Server adds file append, future ring buffer; client inherits same call surface |
| Persistence | Universe directory + ConfigNode files | KSP-native format; replayable via vessel proto files |
| Backup model | Two-tier: in-memory flush + archive snapshots | Operational safety without the cost of constant snapshotting |
| Harmony patches | One file per patched method | Surgical, reviewable, less merge conflict surface |
| AI attribution | Strip everywhere | Upstream community sensitivity (Fierce-Cat / #588) |
| Fork-upstream relationship | Fork-master only, no upstream coordination | Strategy shift 2026-05-16: AdmiralRadish's commits are reference material; we adopt/edit/replace case-by-case. Coordination meta-issue deferred indefinitely. |
| Protocol version | 0.30.0 (bumped from 0.29.1) | Cross-subspace lock keying (BUG-005/006) is a clean break; vanilla 0.29.x peers no longer cross-compatible. |
| Fork-local vessel metadata | `lmp*`-prefixed top-level ConfigNode fields | KSP vessel loaders ignore unknown fields; `lmp` prefix means our additions round-trip safely through any KSP-side persistence path. |

---

## Stack Notes & Patterns Learned

_Each entry has a date and the context that prompted it. Don't relearn these._

- **`RunBackup` is a flush, not a snapshot** (2026-05-16): existing `BackupSystem.RunBackup()` writes in-memory server state to the canonical Universe files. It is **not** a separate copy. The new `RunArchiveBackup()` (commit `f4aed253`) is the actual snapshot-with-retention path. Do not conflate them in naming, comments, or call graphs.
- **`LunaLog.Normal` is the project's info level** (2026-05-16): existing convention from `BackupCommand` and friends. Don't switch to `LunaLog.Info` for that role without a deliberate reason — `Info` exists but is reserved for emphasis (white text).
- **`LunaLog.LogFilename` is mutably reassigned across threads** (2026-05-16): `LogThread.cs` reassigns it on day rollover while `AfterPrint` reads it from network/system threads. Latent race; not currently observed, but don't make it worse without fixing it properly.
- **System `dotnet` is the wrong dotnet** (2026-05-16): typical Windows installs ship `dotnet` 7.x, which cannot satisfy the `10.0.100` pin in `global.json`. Use the user-installed `.NET 10 SDK` at `C:\Users\austi\.dotnet\`.
- **Singletons are heavily referenced but also heavily patched** (2026-05-16): `Funding.Instance` and friends appear ~83 times. Harmony interception is already a pattern (e.g., `ContractPreLoader_Filter.cs`). Per-agency work in Stage 5 means enumerating every read/write site and patching the singleton accessor — wide-but-shallow, not architecturally hard.
- **Upstream is actively revived by AdmiralRadish** (2026-05-16): 21 commits + 17 merged PRs since April 2026 across docking, coupling, scenario sync, lock handoff. Always `git fetch upstream` and check `upstream/master..HEAD` before touching those areas. We coordinate; we do not duplicate.
- **PlagueNZ split-progression fork is benchmark only** (2026-05-16): 113 commits ahead of upstream, 33 releases over 6 weeks, alpha-quality, bus factor 1. We compare our ground-up per-agency design against theirs but do not cherry-pick from them — the goal is to learn the architecture deeply, not inherit their decisions.
- **Fork-master strategy supersedes upstream coordination** (2026-05-16, session 3): all work happens on `master` of our fork. We observe `git log upstream/master --author=AdmiralRadish` for reference but do NOT post coordination issues up front. Decision per-fix: adopt his work verbatim, edit it, or replace it. Upstream PRs deferred until/unless explicitly revisited. The coordination meta-issue at `C:\tmp\luna-coordination-issue.md` is retained on disk but not posted.
- **Protocol bumped to 0.30.0** (2026-05-16, session 3, commit `d64acf66`): cross-subspace lock keying (BUG-005/006) restored the `SendUnloadedSecondary*` broadcasts that upstream `fbc7a8c` disabled. Mixing a fork 0.30.0 peer with a vanilla 0.29.x peer corrupts vessel state. The `(0,30,0,29)` cross-compat row was removed from `LmpCommon/LmpVersioning.cs`. Future protocol bumps need an equally significant break to justify.
- **`lmpAuthSubspace` is the canonical fork-metadata field on vessel ConfigNodes** (2026-05-16, session 3): the `lmp*` prefix means KSP's vessel loader silently ignores the unknown field, so our additions round-trip safely. Stored via the existing `MixedCollection<string, string> Fields` on `Server/System/Vessel/Classes/Vessel.cs`. Future fork-local vessel metadata MUST use the same prefix convention.
- **`Server/ForkBuildInfo.cs` is the registry of fork-applied fixes** (2026-05-16, session 3, commit `d2186e2e`): `ActiveFixes[]` lists every fix in commit-chronological order; `MainServer.Main` emits a `[fork] ...` banner at boot. Every runtime fix-related log line uses `[fix:BUG-XXX]` prefix so operators can `grep -F "[fix:"` to find fork-attributed events. When adding a new fix: append to `ActiveFixes[]` AND prefix the runtime log lines.
- ~~**LmpClient cannot be built locally without the .NET Framework 4.7.2 dev pack** (2026-05-16, session 3)~~ — **OBSOLETE 2026-05-16, session 4**: LmpClient now builds locally. Prereqs are documented in the "Build & Run → LmpClient" section: install the .NET Framework 4.7.2 Developer Pack and populate `External/KSPLibraries/` from a local KSP install (`KSP_x64_Data/Managed/`). `LmpClient/Directory.Build.props` wires `TargetFrameworkRootPath` so the SDK MSBuild finds the v4.7.2 targeting pack. Stage 2 client-side fixes that previously shipped "reviewed not compiled" (BUG-003/004 interp cap, BUG-051b retry, BUG-005/006 restored `SendUnloadedSecondary*`) are now compile-verified locally.
- **`Server/System/Vessel/VesselSanitizer.cs` is the central proto-vessel sanitiser** (2026-05-16, session 5, commit `c5ab8fa5`): defensive normalisation of inbound proto-vessel ConfigNodes runs inside `VesselDataUpdater.RawConfigNodeInsertOrUpdate` so neither the universe-on-disk copy nor any downstream relay carries the bad payload. Currently handles the BUG-013 reaction-wheel `stateString` locale problem; when a future KSP-localisation-bleed-through bug appears (BUG-013 family), add a new whitelist + module-name guard to this file rather than scattering one-offs. Idempotent on clean vessels; logs once per affected vessel with `[fix:BUG-013]`.
- **Vendored Lidgren has a real shutdown-race NRE** (2026-05-16, session 5, commit `b7a51ae1`): `NetReliableSenderChannel.DestoreMessage` dereferenced `storedMessage.m_recyclingCount` BEFORE its null check. A late ACK arriving while `NetPeer.ExecutePeerShutdown` was draining the heartbeat killed the host process with no diagnostic — caught only when the mock-client harness shut down NetServer + NetClient concurrently. Fix is local; the `Lidgren/` directory is fork-vendored and not synced from any active upstream, so the patch lives here. If we ever do sync from a maintained Lidgren fork, re-check this site.
- **Mock-client harness is single-instance per process** (2026-05-16, session 5): `ServerHarness` brings the Server up via static singletons (`ServerContext`, `WarpContext`, the 12 `*.SettingsStore`, the singleton `NetPeerConfiguration`). Only one harness instance per test process — use `[AssemblyInitialize]` / `[AssemblyCleanup]` boundaries. Per-test state resets in `[TestInitialize]` via `ServerHarness.ResetPerTestState` (currently clears `ServerContext.Clients` + `WarpSystem.Reset()` + `WarpRequestCache.Clear()`). Add further per-fix state clears there as new harness tests need them. **Do NOT** override `WarpContext.NextSubspaceId` — it races with `LoadSavedSubspace`'s pre-seed and causes silent `TryAdd` no-ops.

_Append new entries chronologically. If a note becomes obsolete, prefer striking it through with a date rather than deleting outright, so future-you sees the lesson._

---

## Known Limitations & Future Work

- **Logging:** ring buffer + tagged overloads shipped Stage 1.2. Size-based rotation deferred (daily + expire suffice until dashboard ships); `LogSettings.RingBufferSize` setting deferred (`LogRingBuffer.Capacity` is a `const`).
- **Admin dashboard:** Stage 3.7 v1 shipped — `GET /fork` exposes `ForkInformation` (fork name + protocol version + `ActiveFixes[]`); `GET /log` exposes a `LogRingBuffer` snapshot. Both are JSON, both via the new `JsonGetHandler`. v1 is read-only; no HTML, no auth, no filtering. v2 might add level/subsystem query filters and a tiny HTML view — defer until an operator actually asks.
- **Mock-client test harness:** Stage 4.9 v1 shipped (session 4). `MockClientTest` brings the real Server up in-process on a free UDP port; `MockNetClient` completes the LMP handshake against it. Per-fix regression tests (Stage 4.10) and CI integration (Stage 4.11) are still pending.
- **Per-agency career:** Stage 5, not started. Lives on `feature/per-agency` branch (also not yet created).
- **Pre-existing build warnings (30):** noise to be tackled in a dedicated pass, not piecemeal.
- **`GroupSystem` is scaffolding:** name + member list only, no resource fields. Needed for per-agency.
- **Bug inventory:** see `docs/research/01-bug-inventory.md` for the full 51-bug catalogue (BUG-051 added during Phase-2). Stage 2 closed the top-1 priority plus four others: BUG-001 (solo-subspace), BUG-003/004 (interp cap), BUG-005/006 (cross-subspace lock), BUG-014 (audit-closed via upstream PR #628), BUG-051 (stuck warp). Stage 3 picked up BUG-013 (reaction-wheel stateString sanitiser) and BUG-008 Phase A (PQS-timing realignment); BUG-018 / 019 / 024 audit-closed via upstream PR #687. Remaining top-10: BUG-010 (disconnect-explode), BUG-023 (astronaut complex desync), BUG-025 (R&D double-purchase), BUG-033 (backup race), BUG-045 (Breaking Ground deployable science).
- **BUG-001 rejoin race:** documented known limitation. Solo→non-solo transition may fire one snap because server's `Subspaces[id].Time` is stale relative to the solo player's UT. Follow-up: have the client report its UT delta on rejoin so server can refresh before broadcasting.
- **Couple handoff covers dock only:** `HandleVesselCouple` sets the merged vessel's `AuthoritativeSubspaceId` to the initiator's. Undock relies on the new child vessel's first proto-update to stamp authority via the standard rule — adequate for typical KSP flows but not as explicit as the dock path.

---

## Stage Roadmap

Master plan (also tracked in conversation todos for active work):

- **Stage 1 — Foundations** ✅ COMPLETE
  - ✅ 1.1 Backup archives + restore (`f4aed253`)
  - ✅ 1.2 Logging upgrade — ring buffer + tagged overloads + parse-back capture (`4e3e6bd2`)
  - ⏸ 1.3 Upstream coordination meta-issue — **DEFERRED INDEFINITELY** per fork-master strategy
- **Stage 2 — First visible stability win** ✅ COMPLETE (2026-05-16)
  - ✅ 2.4 Phase-2 analyses for time/subspace bugs (`48df64bd`, `fc2b793a`)
  - ✅ 2.5a BUG-051a server-side request dedup (`9732fc7e`)
  - ✅ 2.5b BUG-001 solo-subspace catch-up (`0f10b2d3`)
  - ✅ 2.5c BUG-003/004 future-subspace interpolation cap (`cd551859`)
  - ✅ 2.5d BUG-051b client steady-state retry (`25303e7d`)
  - ✅ 2.5e BUG-014 audit-closed (`7f1393f4`)
  - ✅ 2.5f BUG-005/006 cross-subspace lock keying + protocol bump 0.30.0 (`d64acf66`)
  - ✅ 2.5g Fork-build banner + `[fix:BUG-XXX]` log tags (`d2186e2e`)
  - ⏸ 2.6 First upstream PR — **DEFERRED** by strategy shift
- **Stage 3 — Operational tooling (3–4 weeks, parallel)** — IN PROGRESS
  - ✅ 3.7 Admin dashboard v1 — `/fork` + `/log` (text/plain, human-readable) + `/logjson` (tooling) endpoints. Backed by `ForkBuildInfo` + `LogRingBuffer`. v2 (level/subsystem filters, HTML view, basic auth) deferred.
  - ⏳ 3.8 Phase-2 + fixes for remaining top-10 — IN PROGRESS. ✅ BUG-013 fix shipped (`c5ab8fa5`, reaction-wheel `stateString` sanitiser). ✅ BUG-008 Phase A shipped (session 6, `PqsAlignmentRoutine` + `VesselProtoSystem` hook) — Phase B (server-side stored `terrainAltitude`) deferred until Phase A soaks. ✅ BUG-018 / BUG-019 / BUG-024 audit-closed via upstream PR #687. Remaining: BUG-010 / BUG-023 / BUG-025 / BUG-033 / BUG-045.
- **Stage 4 — Mock-client test harness (2–4 weeks)** — IN PROGRESS
  - ✅ 4.9 Protocol harness v1 — `MockClientTest` project with `ServerHarness` (real Server in-process on a free localhost UDP port) + `MockNetClient` (Lidgren peer speaking the LMP wire protocol via the production factories). `HandshakeSmokeTest` proves the wiring end-to-end. Design at `docs/research/04-mock-client-harness-design.md`.
  - ✅ 4.10 Regression tests for shipped fixes — server-observable bugs COMPLETE. ✅ BUG-051a (`Bug051aDedupTest`). ✅ BUG-001 (`Bug001SoloBroadcastTest`). ✅ BUG-005/006 (`Bug005SubspaceRejectTest`). Also surfaced + fixed [BUG-052](docs/research/01-bug-inventory.md) (vendored Lidgren `NetReliableSenderChannel.DestoreMessage` NRE on late-ACK during peer shutdown) while writing the first test. BUG-003/004 + BUG-051b are client-internal and need a separate `LmpClientTest` net472 project that references the now-buildable `LmpClient.dll`.
  - 4.11 CI integration
- **Stage 5 — Per-agency career (2–4 months, separate branch)**
  - 5.12 Create `feature/per-agency`
  - 5.13 Audit PlagueNZ (113 commits + 5 issues, benchmark only)
  - 5.14 Server-side per-player state (`Dictionary<player, AgencyState>`)
  - 5.15 Harmony interception layer
  - 5.16 Agency UI
  - 5.17 Craft ownership (`OwningAgency` field + lock enforcement + reward routing)
  - 5.18 Continuous PlagueNZ comparison

---

## Updating This File

- A convention changed? Update it the same session.
- A new system / command / setting landed? Add it under the relevant inventory section.
- A footgun bit you? Note it under "Stack Notes & Patterns Learned" with a date.
- A stage completed or scope shifted? Update "Stage Roadmap."
- AdmiralRadish merged something that affects our turf? Note it under "Stack Notes."

CLAUDE.md is the contract for how this fork is worked on. Stale guidance is worse than no guidance.
