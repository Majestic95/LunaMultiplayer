using Server;
using Server.Context;
using Server.Server;
using Server.Settings;
using Server.Settings.Structures;
using Server.System;
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

            // Pin the loopback port and forbid UPnP / master-server traffic.
            Port = FindFreeUdpPort();
            ConnectionSettings.SettingsStore.Port = Port;
            ConnectionSettings.SettingsStore.ListenAddress = "127.0.0.1";
            ConnectionSettings.SettingsStore.AutoExpandMtu = false;
            GeneralSettings.SettingsStore.ServerName = "harness-server";
            // Don't broadcast to or register with the LMP master servers.
            MasterServerSettings.SettingsStore.RegisterWithMasterServer = false;

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
