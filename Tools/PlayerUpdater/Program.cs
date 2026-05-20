using System;
using System.Threading;
using System.Windows.Forms;

namespace LunaMultiplayer.PlayerUpdater
{
    internal static class Program
    {
        // Global\ prefix puts the mutex in the system-wide kernel namespace so
        // an elevated launch and a non-elevated launch of the same user still
        // collide. Without Global\ the two sessions would each have their own
        // Local\ namespace and a second instance could overlay the same KSP
        // install concurrently.
        //
        // Embedding the project GUID makes the name unique to this binary so
        // we don't collide with any other tool that happens to use the same
        // app-name string.
        private const string SingleInstanceMutexName =
            @"Global\LunaMultiplayer.PlayerUpdater.SingleInstance.{6F8D4E2C-3A5B-4F7E-9C1D-8E2F4A5B6C7D}";

        [STAThread]
        private static int Main()
        {
            using var mutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out var createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "Luna Multiplayer Player Updater is already running.\n\n" +
                    "Check the taskbar for the existing window.",
                    "Already running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 1;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Forms land in a follow-up commit (Piece A — core modules + WinForms).
            // The scaffold proves the project builds, the single-instance mutex
            // works, and the manifest is honoured (asInvoker / PerMonitorV2).
            MessageBox.Show(
                "Luna Multiplayer Player Updater (scaffold).\n\n" +
                "The real updater UI lands in a follow-up commit. This build " +
                "only verifies that the project compiles and the single-instance " +
                "guard works.",
                "PlayerUpdater scaffold",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return 0;
        }
    }
}
