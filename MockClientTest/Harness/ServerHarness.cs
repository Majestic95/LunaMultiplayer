using Server;
using Server.Context;
using Server.Server;
using Server.Settings;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MockClientTest.Harness
{
    /// <summary>
    /// Brings up the real <see cref="Server"/> assembly in-process on a free
    /// localhost UDP port, against a fresh temp Universe/Config directory pair.
    /// Designed for the Stage 4.9 mock-client harness — see
    /// <c>docs/research/04-mock-client-harness-design.md</c> for the design
    /// rationale and the constraints this class works around.
    ///
    /// Single-instance: the Server is heavily static (ServerContext + the 12
    /// settings stores + the singleton NetPeerConfiguration), so only one
    /// ServerHarness can be live in a process. Use <c>[AssemblyInitialize]</c>
    /// to start it and <c>[AssemblyCleanup]</c> to stop it; individual tests
    /// reset per-test state in <c>[TestInitialize]</c>.
    /// </summary>
    public static class ServerHarness
    {
        private static CancellationTokenSource _cts;
        private static Task _receiveTask;
        private static string _tempRoot;
        private static bool _started;

        /// <summary>The loopback port the harness is listening on.</summary>
        public static int Port { get; private set; }

        /// <summary>The temp root the harness scoped Universe/Config under.</summary>
        public static string TempRoot => _tempRoot;

        public static void Start()
        {
            if (_started)
                throw new InvalidOperationException("ServerHarness is already started.");

            _tempRoot = Path.Combine(Path.GetTempPath(), "lmp-harness-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempRoot);
            Directory.CreateDirectory(Path.Combine(_tempRoot, "Config"));
            Directory.CreateDirectory(Path.Combine(_tempRoot, "Universe"));

            // ServerContext caches Universe/Config paths from AppDomain.BaseDirectory
            // at type-load. Override them to point at the temp dir BEFORE any
            // settings load or universe scan touches disk.
            ServerContext.UniverseDirectory = Path.Combine(_tempRoot, "Universe");
            ServerContext.ConfigDirectory = Path.Combine(_tempRoot, "Config");
            ServerContext.ModFilePath = Path.Combine(ServerContext.ConfigDirectory, "LMPModControl.xml");

            // Hop CWD too so any code path that uses relative paths writes
            // into the temp dir instead of polluting the test bin folder.
            Environment.CurrentDirectory = _tempRoot;

            // Settings: writes defaults to Config/ on first load, then reads them back.
            SettingsHandler.LoadSettings();

            // Bootstrap the Universe child folders the same way MainServer.Main does at
            // production boot. The Agencies/ child folder (Stage 5) is what specifically
            // forces this — FileHandler.WriteAtomic does not create parent directories,
            // so AgencySystem.SaveAgency would silently fail without this call. Idempotent
            // on a fresh temp dir; the other folders the production server creates
            // (Vessels, Scenarios, Kerbals, etc.) come along for free and let agency
            // tests share the harness with future write-path tests in this directory.
            //
            // Note: production MainServer.Main also runs the matching loaders
            // (LoadExistingVessels / LoadExistingScenarios / LoadExistingAgencies /
            // GroupSystem.LoadGroups) after CheckUniverse. The harness intentionally
            // skips them — on a fresh temp dir they're no-ops. A future test that
            // pre-seeds Universe files on disk and expects the in-memory registries
            // populated at Start must call the loader explicitly in [TestInitialize].
            Universe.CheckUniverse();

            // Pin the loopback port and forbid UPnP / master-server traffic.
            Port = FindFreeUdpPort();
            ConnectionSettings.SettingsStore.Port = Port;
            ConnectionSettings.SettingsStore.ListenAddress = "127.0.0.1";
            ConnectionSettings.SettingsStore.AutoExpandMtu = false;
            GeneralSettings.SettingsStore.ServerName = "harness-server";
            // Don't broadcast to or register with the LMP master servers.
            MasterServerSettings.SettingsStore.RegisterWithMasterServer = false;
            // Disable mod-control allowlist filtering. Production MainServer.Main calls
            // ModFileSystem.LoadModFile when this setting is on, but the harness skips
            // Main, so ModFileSystem.ModControl stays null. With the gate on, the proto-
            // ingest path in VesselDataUpdater dereferences ModControl.AllowedParts and
            // NREs inside the fire-and-forget Task.Run — silently before Stage 5.16b
            // started asserting on post-ingest store contents. Disabling here is simpler
            // than wiring the loader; tests don't need allowlist filtering.
            GeneralSettings.SettingsStore.ModControl = false;

            ServerContext.ServerClock.Restart();
            ServerContext.Day = DateTime.UtcNow.Day;

            // Reset the warp/time/lock state in case a prior test process left
            // anything behind in the static singletons.
            WarpSystem.Reset();
            TimeSystem.Reset();

            // Wire the NetServer with the loopback config and start it listening.
            LidgrenServer.SetupLidgrenServer();
            ServerContext.ServerRunning = true;

            _cts = new CancellationTokenSource();
            _receiveTask = MainServer.LongRunTaskFactory.StartNew(LidgrenServer.StartReceivingMessagesAsync, _cts.Token);

            _started = true;
        }

        public static void Stop()
        {
            if (!_started)
                return;

            try
            {
                ServerContext.ServerRunning = false;
                LidgrenServer.ShutdownLidgrenServer();
                _cts?.Cancel();
                _receiveTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Teardown is best-effort — the test process is about to exit.
            }

            try
            {
                if (_tempRoot != null && Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
                // Leaving the temp dir behind is harmless; the OS sweeps eventually.
            }

            _started = false;
        }

        /// <summary>Wait until the NetServer reports a started status. Cheap polled loop.</summary>
        public static bool WaitUntilListening(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (LidgrenServer.Server != null && LidgrenServer.Server.Status == Lidgren.Network.NetPeerStatus.Running)
                    return true;
                Thread.Sleep(20);
            }
            return false;
        }

        /// <summary>Removes any clients/state a prior test left in the static dictionaries.</summary>
        public static void ResetPerTestState()
        {
            ServerContext.Clients.Clear();
            // WarpSystem.Reset clears Subspaces and reloads Subspace.txt (which creates
            // subspace 0 with NextSubspaceId=1 on first run). Do NOT override NextSubspaceId
            // here — that races with LoadSavedSubspace and causes silent TryAdd no-ops.
            WarpSystem.Reset();
            WarpRequestCache.Clear();
            VesselStoreSystem.CurrentVessels.Clear();
            // [fix:BUG-025] Bug025RejectionTest seeds a tech ID into the canonical
            // R&D scenario before running. Clear here so a follow-on test sees
            // the boot-default empty state, not the prior test's seed.
            ScenarioStoreSystem.CurrentScenarios.Clear();
            // [Stage 5.16a] Drop any per-agency registry entries a prior test left
            // behind, AND force the gate back off. Agency tests opt in by setting
            // PerAgencyCareer=true in their own [TestInitialize] AFTER this reset
            // runs, so a forgotten test that leaves the flag on cannot leak into
            // a follow-on non-agency test (which would then start broadcasting
            // Handshake/State messages it doesn't expect).
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
        }

        private static int FindFreeUdpPort()
        {
            // Bind a UDP socket to port 0; OS picks a free ephemeral port. Close it
            // immediately and reuse the number. There's a tiny TOCTOU window between
            // releasing and rebinding but it's harmless inside a controlled test run.
            using (var probe = new UdpClient(0, AddressFamily.InterNetwork))
            {
                return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            }
        }

        // Cheap escape hatch for tests that want to inspect a private/internal
        // member without us widening surface area in production code. Currently
        // unused; kept here so future regression tests have a way in.
        internal static object GetStaticMember(Type type, string name)
        {
            var f = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(null);
            var p = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null) return p.GetValue(null);
            throw new MissingMemberException(type.FullName, name);
        }
    }
}
