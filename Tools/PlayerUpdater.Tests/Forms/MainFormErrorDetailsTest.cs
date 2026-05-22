using LunaMultiplayer.PlayerUpdater.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Forms
{
    // Tests for MainForm.BuildErrorDetails — the pure-helper that composes
    // the ErrorDialog `details` body from the pipeline error string plus an
    // optional install-log path. The method is small and the tests stay
    // tight, but they pin a behaviour the cohort cares about: when the
    // install log exists, its path MUST appear in the dialog so the
    // operator can find the per-entry trail. Without this, the
    // InstallLogWriter wiring would be silently disconnected from the UX.
    [TestClass]
    public class MainFormErrorDetailsTest
    {
        [TestMethod]
        public void BuildErrorDetails_NoLogPath_ReturnsErrorOnly()
        {
            var text = MainForm.BuildErrorDetails(
                pipelineError: "Extract completed with 3 failed entries...",
                logPath: null);

            Assert.AreEqual("Extract completed with 3 failed entries...", text);
        }

        [TestMethod]
        public void BuildErrorDetails_WithLogPath_AppendsPath()
        {
            var text = MainForm.BuildErrorDetails(
                pipelineError: "Extract completed with 3 failed entries...",
                logPath: @"C:\PlayerUpdater\install-log-2026-05-22-183000Z.txt");

            // Exact-string assertion so a future refactor that drops a
            // newline / reorders the segments / drops the leading two-space
            // indent (which RollbackDialog and other dialogs use as a
            // path-styling convention) trips the test instead of silently
            // breaking the operator-facing copy-paste UX.
            Assert.AreEqual(
                "Extract completed with 3 failed entries...\n\n" +
                "Per-entry log written to:\n" +
                "  C:\\PlayerUpdater\\install-log-2026-05-22-183000Z.txt",
                text);
        }

        [TestMethod]
        public void BuildErrorDetails_NullError_NoLogPath_RendersPlaceholder()
        {
            var text = MainForm.BuildErrorDetails(pipelineError: null, logPath: null);

            Assert.AreEqual("(no diagnostic)", text);
        }

        [TestMethod]
        public void BuildErrorDetails_EmptyError_WithLogPath_RendersPlaceholderPlusPath()
        {
            // Defensive: even if the pipeline ever returns an empty Error
            // string, the log path should still appear so the operator
            // can attach it to a bug report.
            var text = MainForm.BuildErrorDetails(
                pipelineError: "",
                logPath: @"C:\install-log.txt");

            StringAssert.Contains(text, "(no diagnostic)");
            StringAssert.Contains(text, @"C:\install-log.txt");
        }

        [TestMethod]
        public void BuildErrorDetails_EmptyLogPath_TreatedAsAbsent()
        {
            // The MainForm caller can pass either null or empty when the
            // log-write was skipped; both should produce error-only output
            // rather than rendering an empty "log path: " line.
            var text = MainForm.BuildErrorDetails(
                pipelineError: "Some error",
                logPath: "");

            Assert.AreEqual("Some error", text);
        }
    }
}
