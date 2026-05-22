using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LunaMultiplayer.PlayerUpdater.Core;

namespace LunaMultiplayer.PlayerUpdater.Forms
{
    // Modal dialog that drives one InstallPipeline.InstallAsync call to
    // completion. The form is opened with `ShowDialog(owner)` — it returns
    // when the pipeline returns (success, refusal, or cancellation).
    //
    // The pipeline returns InstallResult on every non-cancel outcome; the
    // form exposes that as the Result property. A null Result means the
    // operator cancelled mid-install (OperationCanceledException propagated
    // out of InstallAsync), in which case the partial backup (if any) is
    // left on disk with its in-progress marker so the next launch surfaces
    // the rescue prompt.
    //
    // We construct a NEW InstallPipeline per dialog because the pipeline
    // owns an HttpDownloader (one socket pool per dialog is fine — we don't
    // do back-to-back installs in the same session).
    //
    // Progress callbacks come from the pipeline via IProgress<InstallProgress>;
    // System.Progress<T> captures the SynchronizationContext at construction
    // time, and because we construct it on the UI thread inside the form,
    // callbacks land on the UI thread automatically. No Invoke() needed.
    internal sealed class ProgressForm : Form
    {
        private readonly InstallRequest _request;
        private readonly CancellationTokenSource _cts = new();
        private readonly Label _stageLabel = new()
        {
            AutoSize = true,
            Text = "Preparing…",
            MaximumSize = new Size(420, 0),
        };
        private readonly Label _detailLabel = new()
        {
            AutoSize = true,
            Text = "",
            MaximumSize = new Size(420, 0),
            ForeColor = SystemColors.GrayText,
        };
        private readonly ProgressBar _progressBar = new()
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Dock = DockStyle.Fill,
            Height = 24,
        };
        private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true };

        public InstallResult? Result { get; private set; }

        public ProgressForm(InstallRequest request)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));

            Text = "Installing…";
            ClientSize = new Size(460, 180);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            ControlBox = false; // operator must use the Cancel button, not the X

            BuildLayout();

            _cancelButton.Click += OnCancelClicked;
            Shown += OnFormShown;
            FormClosing += OnFormClosing;
        }

        private void BuildLayout()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16),
            };
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(_stageLabel, 0, 0);
            grid.Controls.Add(_detailLabel, 0, 1);
            grid.Controls.Add(_progressBar, 0, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0),
            };
            buttons.Controls.Add(_cancelButton);
            grid.Controls.Add(buttons, 0, 3);

            Controls.Add(grid);
        }

        private async void OnFormShown(object? sender, EventArgs e)
        {
            // System.Progress<T> dispatches on the SynchronizationContext
            // captured at construction time. We're on the UI thread here,
            // so callbacks marshal to the UI thread automatically.
            var progress = new Progress<InstallProgress>(OnProgress);
            using var pipeline = new InstallPipeline();

            try
            {
                Result = await pipeline.InstallAsync(_request, progress, _cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Operator cancelled. Result stays null. The partial backup
                // (if any) is preserved on disk with its in-progress marker.
                Result = null;
            }
            catch (Exception ex)
            {
                // Catch-all net for any exception type the pipeline can
                // surface that isn't already mapped to an InstallResult.
                // InstallPipeline.InstallAsync documents most refusal paths
                // as InstallResult outcomes, but defence-in-depth here
                // prevents an un-mapped exception from crashing the whole
                // process via the async-void boundary (consumer-lens
                // MUST FIX #3). We keep OperationCanceledException above
                // so cancellation doesn't fall into this branch.
                Result = new InstallResult(
                    Outcome: ex is ArgumentException
                        ? InstallOutcome.DownloadFailed
                        : InstallOutcome.ExtractFailed,
                    BackupDir: null,
                    OverlayResult: null,
                    HashSkipped: false,
                    Error: $"Unexpected error during install: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Close();
            }
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            // Belt-and-braces: ControlBox=false hides the X but Alt+F4
            // still fires FormClosing on this form. Make sure cancellation
            // fires whenever the form is closing for any reason so the
            // in-flight HTTP request and file-copy loops unwind cleanly.
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts.Dispose();
            base.Dispose(disposing);
        }

        private void OnCancelClicked(object? sender, EventArgs e)
        {
            _cancelButton.Enabled = false;
            _stageLabel.Text = "Cancelling…";
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        // Called on the UI thread (Progress<T> auto-marshalling).
        // BytesProcessed/TotalBytes are meaningful only during Downloading;
        // other stages report (0, asset.Size) per the InstallPipeline
        // contract.
        private void OnProgress(InstallProgress p)
        {
            _stageLabel.Text = StageLabel(p.Stage);

            if (p.Stage == InstallStage.Downloading && p.TotalBytes > 0)
            {
                // Switch from marquee to determinate progress.
                if (_progressBar.Style != ProgressBarStyle.Continuous)
                {
                    _progressBar.Style = ProgressBarStyle.Continuous;
                    _progressBar.Minimum = 0;
                    _progressBar.Maximum = 1000;
                }
                var fraction = Math.Clamp(p.BytesProcessed / (double)p.TotalBytes, 0.0, 1.0);
                _progressBar.Value = (int)(fraction * 1000);
                _detailLabel.Text = $"{FormatBytes(p.BytesProcessed)} of {FormatBytes(p.TotalBytes)} — {fraction * 100:F1}%";
            }
            else
            {
                // Non-download stages or unknown-size download: marquee.
                if (_progressBar.Style != ProgressBarStyle.Marquee)
                {
                    _progressBar.Style = ProgressBarStyle.Marquee;
                }
                _detailLabel.Text = string.IsNullOrWhiteSpace(p.CurrentItem) ? "" : p.CurrentItem;
            }
        }

        private static string StageLabel(InstallStage stage) => stage switch
        {
            InstallStage.Preparing => "Preparing — checking disk space…",
            InstallStage.Downloading => "Downloading the release zip…",
            InstallStage.Verifying => "Verifying SHA-256…",
            InstallStage.BackingUp => "Backing up files about to be overwritten…",
            InstallStage.Extracting => "Extracting the new files…",
            InstallStage.Finalizing => "Finalising the install…",
            _ => "Working…",
        };

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
