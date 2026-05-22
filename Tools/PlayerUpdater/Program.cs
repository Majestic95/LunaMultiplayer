using System;
using System.Threading;
using System.Windows.Forms;
using LunaMultiplayer.PlayerUpdater.Forms;

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

            // SetCompatibleTextRenderingDefault MUST be called before
            // EnableVisualStyles per Microsoft docs — reversing the order
            // produces undefined behaviour on text-rendering paths used by
            // older controls. Set DPI mode first (required before any
            // window is created), then the two text/style toggles in their
            // documented order.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetCompatibleTextRenderingDefault(false);
            Application.EnableVisualStyles();

            // Async-void event handlers in MainForm / ProgressForm cannot
            // be reached by the synchronous catch around Application.Run.
            // Wire the WinForms thread-exception event AND the AppDomain
            // unhandled-exception event so:
            //   - exceptions on the UI thread land in ShowCrashDialog
            //   - exceptions on a worker thread (rare — Core uses async
            //     but the continuations marshal back to the UI thread)
            //     also land in ShowCrashDialog before the process dies.
            // Application.SetUnhandledExceptionMode(CatchException) routes
            // ALL exceptions through Application.ThreadException, even
            // those that would otherwise crash via the AppDomain path.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, args) => ShowCrashDialog(args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex) ShowCrashDialog(ex);
            };

            try
            {
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                // Top-level guard for SYNCHRONOUS construction throws (e.g.
                // a MainForm constructor failure). Async-void handler
                // exceptions are caught by the ThreadException wire-up
                // above and never reach here.
                ShowCrashDialog(ex);
                return 2;
            }
        }

        // Renders a copy-paste-friendly crash dialog. Process exits after
        // the operator dismisses — we do NOT keep running post-exception
        // because any in-flight install / file copy is in an undefined
        // state and continuing risks compounding the damage.
        private static void ShowCrashDialog(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "Luna Multiplayer Player Updater encountered an unhandled error and must close.\n\n" +
                    $"{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"{ex.StackTrace}\n\n" +
                    "Copy this message body into the cohort Discord support channel.",
                    "PlayerUpdater error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // Last-ditch — if the dialog itself fails (rare; usually
                // means the UI thread is in an exotic state), there's
                // nothing left to do.
            }
            Environment.Exit(2);
        }
    }
}
