using System;
using System.Drawing;
using System.Windows.Forms;

namespace LunaMultiplayer.PlayerUpdater.Forms
{
    // Modal error dialog with a single-line summary, an expanded details
    // textbox, and a Copy-details button. Optionally offers a Rollback
    // button (DialogResult.Retry) when a backup is available — the caller
    // wires that button to BackupManager.RestoreBackup.
    //
    // The Copy button puts the full body (title + summary + details) on the
    // clipboard so the player can paste into a chat/issue. We don't try to
    // hyperlink the issue tracker — that's a future UX nicety, and operator
    // feedback to date is that the cohort copies into Discord by hand.
    internal sealed class ErrorDialog : Form
    {
        private readonly string _title;
        private readonly string _summary;
        private readonly string _details;
        private readonly bool _offerRollback;

        private readonly Label _summaryLabel = new()
        {
            AutoSize = true,
            MaximumSize = new Size(500, 0),
        };
        private readonly TextBox _detailsBox = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };
        private Button _copyButton = null!;
        private readonly System.Windows.Forms.Timer _copyFeedbackTimer = new() { Interval = 1500 };

        public ErrorDialog(string title, string summary, string details, bool offerRollback)
        {
            _title = title ?? throw new ArgumentNullException(nameof(title));
            _summary = summary ?? throw new ArgumentNullException(nameof(summary));
            _details = details ?? throw new ArgumentNullException(nameof(details));
            _offerRollback = offerRollback;

            Text = title;
            ClientSize = new Size(540, 320);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(420, 240);
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            _summaryLabel.Text = summary;
            _detailsBox.Text = details.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

            BuildLayout();

            // Reset the Copy button text after the feedback flash.
            _copyFeedbackTimer.Tick += (_, _) =>
            {
                _copyFeedbackTimer.Stop();
                _copyButton.Text = "Copy details";
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _copyFeedbackTimer.Dispose();
            base.Dispose(disposing);
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
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(_summaryLabel, 0, 0);
            grid.Controls.Add(new Label
            {
                Text = "Details:",
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 4),
            }, 0, 1);
            grid.Controls.Add(_detailsBox, 0, 2);

            _copyButton = new Button { Text = "Copy details", AutoSize = true };
            _copyButton.Click += OnCopyClicked;
            var closeButton = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0),
            };
            buttons.Controls.Add(closeButton);
            if (_offerRollback)
            {
                var rollbackButton = new Button { Text = "Rollback", AutoSize = true, DialogResult = DialogResult.Retry };
                buttons.Controls.Add(rollbackButton);
            }
            buttons.Controls.Add(_copyButton);

            grid.Controls.Add(buttons, 0, 3);

            Controls.Add(grid);
            CancelButton = closeButton;
            AcceptButton = closeButton;
        }

        private void OnCopyClicked(object? sender, EventArgs e)
        {
            var body = $"{_title}{Environment.NewLine}{Environment.NewLine}" +
                       $"{_summary}{Environment.NewLine}{Environment.NewLine}" +
                       $"Details:{Environment.NewLine}{_details}";
            try
            {
                Clipboard.SetText(body);
                _copyButton.Text = "Copied!";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard intermittently locked by another process —
                // surface the failure so the operator knows to retry
                // (was previously silent — Consumer #20 / Upgrade #10).
                _copyButton.Text = "Copy failed — retry";
            }
            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer.Start();
        }
    }
}
