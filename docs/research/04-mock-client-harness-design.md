# Mock-client harness — design (Stage 4.9 v1)

## Goal

Stage 2 shipped three client-side fixes (BUG-003/004 interp cap, BUG-051b
retry, BUG-005/006 restored `SendUnloadedSecondary*` broadcasts) plus
several server-side fixes that depend on multi-peer interaction
(BUG-051a dedup, BUG-001 solo detection, BUG-005/006 server-side
authority check + lock keying). None of these are covered by the
existing unit tests in `ServerTest/`, which exercise individual
classes in isolation.

The mock-client harness exists to put a real LMP server in-process,
attach one or more programmable Lidgren peers, and exercise the
end-to-end wire protocol — so the next regression in any of those
flows fails a test instead of soaking in production.

## Constraints we have to live with

- **Heavy static state.** `ServerContext`, `WarpContext`, all 12 `*SettingsStore`
  singletons, the `NetPeerConfiguration` instance — none of it is per-instance.
  Tests cannot run in parallel against the same server; the harness has to
  serialize them or fully reset state between tests.
- **`NetPeerConfiguration` is one-shot.** Once a Lidgren `NetServer` starts,
  the config is frozen. Restarting the server within a test process means
  the existing config has to be re-applied to a fresh `NetServer`.
- **LmpClient cannot be referenced directly from a `net10.0` project.** It
  targets `net472` and references KSP/Unity assemblies that don't exist
  outside KSP. So the mock client cannot import LmpClient's connection
  code — it has to implement the wire protocol against `LmpCommon` directly.
- **Server boot does disk IO.** `SettingsHandler.LoadSettings` reads / writes
  XML in the working directory, `VesselStoreSystem.LoadExistingVessels`
  scans `Universe/`. The harness uses a per-test temp directory and points
  `ServerContext.UniverseDirectory` + `ServerContext.ConfigDirectory` at it.

## v1 scope (this session)

In-scope:

1. `MockClientTest/` project (net10.0, MSTest) sitting next to `ServerTest/`.
2. `ServerHarness` — brings up a real `Server` on a random localhost
   port using a fresh temp `Universe/` + `Config/`, exposes `Start` /
   `Stop`, and resets the static state that would otherwise leak
   between tests.
3. `MockNetClient` — thin Lidgren `NetClient` wrapper that uses the
   shared `ClientMessageFactory` to send LMP messages and the shared
   `ServerMessageFactory` to deserialize incoming ones; exposes
   `Connect`, `SendMessage<T>`, and `WaitForMessage<T>(timeout)`.
4. One smoke test: a mock client connects, sends a `HandshakeRequest`,
   and asserts the server's `HandshakeReply` has `HandshookSuccessfully`.
   Proves the harness end-to-end.

Out of scope (future work, tracked as followup items):

- BUG-051a regression test — duplicate `WarpNewSubspaceMsgData` with
  same `RequestSeq` returns the same subspace. Needs handshake +
  subsystem subscribe / sync time + warp request flow.
- BUG-001 regression test — solo subspace detection broadcast.
- BUG-005/006 regression test — past-subspace vessel proto rejection.
  Needs two mock clients in different subspaces, plus a vessel proto
  message stub.
- BUG-003/004 + BUG-051b client-internal tests — these are *client*
  computations, not server interactions. They want direct unit tests
  on the `VesselPositionUpdate` / `WarpSystem` classes. That requires
  either a separate `LmpClientTest` project (net472) or restructuring
  the testable logic into `LmpCommon`. Out of scope here.
- CI integration (Stage 4.11).

## Lifecycle model

Per test class:

```
[ClassInitialize] -> ServerHarness.Start(out int port)
                     • picks a free UDP port
                     • creates a temp dir with Config + Universe subdirs
                     • points ServerContext at the temp dir
                     • SettingsHandler.LoadSettings()
                     • LidgrenServer.SetupLidgrenServer()
                     • starts the receive loop + a minimum set of background
                       tasks needed by the test scenario
                     • returns once the server reports ready
[TestInitialize]  -> reset per-test state (clears WarpContext.Subspaces,
                     ServerContext.Clients, LogRingBuffer, etc.)
[TestMethod]      -> exercise via MockNetClient instances
[ClassCleanup]    -> ServerHarness.Stop()
                     • signals ServerRunning = false
                     • shuts down NetServer
                     • cancels the background tasks
                     • deletes the temp dir
```

Per test: each test owns its `MockNetClient` instances and disposes
them in a `finally`. Tests run **sequentially** by default — MSTest's
`TestClass` parallelism is implicitly off for any class that depends
on global state.

## Decision log

- **In-process Server vs out-of-process subprocess.** In-process. Faster
  startup, easier to assert on internal state. Cost is the static-state
  pain; we accept it.
- **`MockNetClient` vs subclassing LmpClient's connection logic.**
  Standalone implementation. LmpClient can't be referenced (net472 +
  KSP deps), and even if it could the client's connection logic is
  entangled with the KSP UI / Mainsystem update loop.
- **MSTest vs xUnit.** MSTest — matches the existing `ServerTest`
  project. No reason to introduce a second test framework.
- **One project vs folded into ServerTest.** Separate. The harness is
  going to grow networking concerns that don't belong in unit-test
  land, and the separation makes the boundary obvious to future
  contributors.

## File layout (v1)

```
MockClientTest/
├── MockClientTest.csproj
├── Harness/
│   ├── ServerHarness.cs        # in-process Server lifecycle
│   └── MockNetClient.cs        # Lidgren peer + send/wait helpers
└── HandshakeSmokeTest.cs       # the proof-it-works test
```

Future tests sit alongside `HandshakeSmokeTest.cs` (e.g. `Bug051aDedupTest.cs`).
