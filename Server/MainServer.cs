using LmpCommon;
using LmpCommon.Time;
using Server.Client;
using Server.Command;
using Server.Context;
using Server.Events;
using Server.Exit;
using Server.Log;
using Server.Plugin;
using Server.Server;
using Server.Settings;
using Server.Settings.Structures;
using Server.System;
using Server.Upnp;
using Server.Utilities;
using Server.Web;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Server
{
    public class MainServer
    {
        public static readonly TaskFactory LongRunTaskFactory = new TaskFactory(TaskCreationOptions.LongRunning, TaskContinuationOptions.None);

        private static readonly ManualResetEvent QuitEvent = new ManualResetEvent(false);

        private static readonly WinExitSignal ExitSignal = OperatingSystem.IsWindows() ? new WinExitSignal() : null;

        private static readonly List<Task> TaskContainer = new List<Task>();

        public static readonly CancellationTokenSource CancellationTokenSrc = new CancellationTokenSource();

        private static bool IsRestart = false;

        public static async Task Main()
        {
            //Verify the .NET runtime before anything else so we can give users a clear,
            //actionable message instead of failing later with a confusing error.
            DotNetRuntimeChecker.EnsureCorrectRuntimeOrExit();

            try
            {
                // Force culture to en-US to avoid 'System.Net.Sockets.resources' assembly load error.
                var ci = new CultureInfo("en-US");
                Thread.CurrentThread.CurrentCulture = ci;
                Thread.CurrentThread.CurrentUICulture = ci;

                if (OperatingSystem.IsWindows())
                    Console.Title = $"LMP {LmpVersioning.CurrentVersion}";

                Console.OutputEncoding = Encoding.UTF8;

                LunaLog.Info("Remember! Quit the server by using 'Control + C' so a backup is properly made before closing!");
                LunaLog.Info("Documentation available at https://github.com/LunaMultiplayer/LunaMultiplayer/wiki");

                if (OperatingSystem.IsWindows())
                    ExitSignal.Exit += (sender, args) => _ = ExitAsync();
                else
                {
                    //Register the ctrl+c event and exit signal if we are on linux
                    Console.CancelKeyPress += (sender, args) => _ = ExitAsync();
                }

                //We disable quick edit as otherwise when you select some text for copy/paste then you can't write to the console and server freezes
                //This just happens on windows....
                if (OperatingSystem.IsWindows())
                    ConsoleUtil.DisableConsoleQuickEdit();

                //We cannot run more than 6 instances ofd servers + clients as otherwise the sync time will fail (30 seconds / 5 seconds = 6) but we use 3 for safety
                if (GetRunningInstances() > 3)
                    throw new HandledException("Cannot run more than 3 servers at a time!");

                //Start the server clock
                ServerContext.ServerClock.Start();

                ServerContext.ServerStarting = true;

                //Set day for log change
                ServerContext.Day = LunaNetworkTime.Now.Day;

                LunaLog.Normal($"Luna Server version: {LmpVersioning.CurrentVersion} ({AppContext.BaseDirectory})");
                LunaLog.Normal($"[fork] {ForkBuildInfo.ForkName} — fixes active: {string.Join(" ", ForkBuildInfo.ActiveFixes)}");

                Universe.CheckUniverse();
                LoadSettingsAndGroups();

                //[perf:relay-scene Phase 1] Per-feature state diagnostic after settings
                //load so operators can grep `[perf:relay-scene]` to verify the gate state.
                //The ForkBuildInfo banner above lists "perf:relay-scene" as one of N
                //tokens — which only tells you the binary CAN do it, not whether it's
                //ACTIVE. The consumer-lens review flagged this as a MUST FIX: an operator
                //setting SceneAwareRelayEnabled=false would otherwise see no signal that
                //they're back on the baseline RelayMessage path.
                if (OptimizationSettings.SettingsStore.SceneAwareRelayEnabled)
                    LunaLog.Normal("[perf:relay-scene] enabled — vessel Position/Flightstate/Update/Resource/PartSync*/ActionGroup/Fairing relays will be dropped to clients NOT in Flight or TrackingStation. Set OptimizationSettings.SceneAwareRelayEnabled=false to restore baseline broadcast behaviour.");
                else
                    LunaLog.Normal("[perf:relay-scene] DISABLED via OptimizationSettings.xml — baseline RelayMessage path active (every vessel relay fans to every client regardless of recipient scene).");

                //[perf:relay-body Phase 2] Per-feature state diagnostic for same-body
                //filtering. Independent of [perf:relay-scene] above — both compose in
                //RelayMessageToFlightSceneSameBody; either can be operator-disabled.
                if (OptimizationSettings.SettingsStore.SameBodyFilterEnabled)
                    LunaLog.Normal("[perf:relay-body] enabled — vessel relays will be dropped when sender and recipient are at different celestial bodies. Conservative same-body-only filter (Mun-from-Kerbin-orbit IS dropped — modded planet packs handled). Set OptimizationSettings.SameBodyFilterEnabled=false to restore scene-only Phase 1 behaviour.");
                else
                    LunaLog.Normal("[perf:relay-body] DISABLED via OptimizationSettings.xml — same-body filtering inactive (Phase 1 scene gate still applies per [perf:relay-scene] above).");

                //[perf:relay-cadence Phase 3] Per-vessel cadence throttle by lock holder.
                //Independent from Phase 1 / 2 — only affects Position relays for vessels
                //with no active Control lock.
                var cadenceMultiplier = OptimizationSettings.SettingsStore.UnpilotedVesselCadenceMultiplier;
                if (cadenceMultiplier > 1)
                {
                    var secondaryMs = IntervalSettings.SettingsStore.SecondaryVesselUpdatesMsInterval;
                    //Cast to long mirrors the hot-path math in MessageQueuer.ShouldRelayPositionByCadence —
                    //prevents wraparound-negative log lines under absurd operator dials (Phase 3 review S1).
                    LunaLog.Normal($"[perf:relay-cadence] enabled — Position relays for vessels without an active Control lock throttled to one per ~{(long)secondaryMs * cadenceMultiplier}ms (SecondaryVesselUpdatesMsInterval={secondaryMs}ms × UnpilotedVesselCadenceMultiplier={cadenceMultiplier}). Set UnpilotedVesselCadenceMultiplier=1 in OptimizationSettings.xml to disable.");
                }
                else
                    LunaLog.Normal("[perf:relay-cadence] DISABLED via OptimizationSettings.xml (UnpilotedVesselCadenceMultiplier<=1) — all Position relays fire at full cadence regardless of Control lock state.");

                VesselStoreSystem.LoadExistingVessels();
                var scenariosCreated = ScenarioSystem.GenerateDefaultScenarios();
                ScenarioStoreSystem.LoadExistingScenarios(scenariosCreated);
                LmpPluginHandler.LoadPlugins();
                WarpSystem.Reset();
                TimeSystem.Reset();

                LunaLog.Normal($"Starting '{GeneralSettings.SettingsStore.ServerName}' on Address {ConnectionSettings.SettingsStore.ListenAddress} Port {ConnectionSettings.SettingsStore.Port}... ");

                LidgrenServer.SetupLidgrenServer();
                await LmpPortMapper.OpenLmpPortAsync();
                await LmpPortMapper.OpenWebPortAsync();
                ServerContext.ServerRunning = true;
                WebServer.StartWebServer();

                //Do not add the command handler thread to the TaskContainer as it's a blocking task
                _ = LongRunTaskFactory.StartNew(CommandHandler.ThreadMainAsync, CancellationTokenSrc.Token);

                TaskContainer.Add(LongRunTaskFactory.StartNew(WebServer.RefreshWebServerInformationAsync, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(LmpPortMapper.RefreshUpnpPortAsync, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(LogThread.RunLogThreadAsync, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(ClientMainThread.ThreadMainAsync, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(() => BackupSystem.PerformBackupsAsync(CancellationTokenSrc.Token), CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(() => BackupSystem.PerformArchiveBackupsAsync(CancellationTokenSrc.Token), CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(() => WarpSystem.PerformSoloSubspaceChecksAsync(CancellationTokenSrc.Token), CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(LidgrenServer.StartReceivingMessagesAsync, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(LidgrenMasterServer.RegisterWithMasterServerAsync, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(LidgrenMasterServer.CheckNATTypeAsync, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(VersionChecker.RefreshLatestVersionAsync, CancellationTokenSrc.Token));
                TaskContainer.Add(LongRunTaskFactory.StartNew(VersionChecker.DisplayNewVersionMsgAsync, CancellationTokenSrc.Token));

                TaskContainer.Add(LongRunTaskFactory.StartNew(() => GcSystem.PerformGarbageCollectionAsync(CancellationTokenSrc.Token), CancellationTokenSrc.Token));

                while (ServerContext.ServerStarting)
                    Thread.Sleep(500);

                LunaLog.Normal("All systems up and running. Поехали!");
                LmpPluginHandler.FireOnServerStart();

                QuitEvent.WaitOne();

                LmpPluginHandler.FireOnServerStop();

                LunaLog.Normal("So long and thanks for all the fish!");

                if (IsRestart)
                {
                    //Start new server
                    var serverExePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Server.exe";
                    var newProcLmpServer = new ProcessStartInfo { FileName = serverExePath };
                    Process.Start(newProcLmpServer);
                }
            }
            catch (Exception e)
            {
                LunaLog.Fatal(e is HandledException ? e.Message : $"Error in main server thread, Exception: {e}");
                Console.ReadLine(); //Avoid closing automatically
            }
        }

        private static void LoadSettingsAndGroups()
        {
            LunaLog.Debug("Loading groups...");
            GroupSystem.LoadGroups();
            LunaLog.Debug("Loading settings...");
            SettingsHandler.LoadSettings();
            SettingsHandler.ValidateDifficultySettings();
            DefaultSettingsChecker.WarnIfUsingDefaults();

            if (GeneralSettings.SettingsStore.ModControl)
            {
                LunaLog.Debug("Loading mod control...");
                ModFileSystem.LoadModFile();
            }

            if (OperatingSystem.IsWindows())
            {
                Console.Title += $" ({GeneralSettings.SettingsStore.ServerName})";
#if DEBUG
                Console.Title += " DEBUG";
#endif
            }
        }

        /// <summary>
        /// Return the number of running instances.
        /// </summary>
        private static int GetRunningInstances() => Process.GetProcessesByName("LunaServer.exe").Length;

        /// <summary>
        /// Runs the exit logic
        /// </summary>
        private static async Task ExitAsync()
        {
            LunaLog.Normal("Exiting... Please wait until all threads are finished");
            ExitEvent.Exit();

            await CancellationTokenSrc.CancelAsync();
            await Task.WhenAll(TaskContainer);

            ServerContext.Shutdown("Server is shutting down");

            QuitEvent.Set();
        }

        /// <summary>
        /// Runs the restart logic
        /// </summary>
        public static async Task RestartAsync()
        {
            //Perform Backups
            await BackupSystem.PerformBackupsAsync(CancellationTokenSrc.Token);
            LunaLog.Normal("Restarting...  Please wait until all threads are finished");

            ServerContext.Shutdown("Server is restarting");
            await CancellationTokenSrc.CancelAsync();
            await Task.WhenAll(TaskContainer);

            IsRestart = true;

            QuitEvent.Set();
        }
    }
}
