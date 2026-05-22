using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LunaMultiplayer.PlayerUpdater.Core;

namespace LunaMultiplayer.PlayerUpdater.Forms
{
    // Modal dialog for the Rollback flow. Shows the list of available
    // backups on the left, the plan-preview (BackupManager.PlanRestore)
    // for the selected backup on the right, and a Restore button at the
    // bottom. On confirm the form sets SelectedBackup and returns OK; the
    // caller (MainForm) handles the actual RestoreBackup call so the
    // dialog stays a pure picker.
    //
    // Plan/Execute symmetry: PlanRestore runs on every selection change so
    // the operator sees exactly what will land on disk before clicking
    // Restore. Same defenses (reparse-point skip + relative-path
    // validation) live inside PlanRestore, so a hostile backup tree cannot
    // mislead the preview either.
    internal sealed class RollbackDialog : Form
    {
        private readonly string _installPath;
        private readonly List<BackupInfo> _backups;

        private readonly ListBox _backupList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox _planPreview = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            BackColor = SystemColors.Window,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };
        private readonly Label _summaryLabel = new() { AutoSize = true, Text = "" };
        private readonly Button _restoreButton = new() { Text = "Restore", AutoSize = true, DialogResult = DialogResult.None, Enabled = false };
        private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        public BackupInfo? SelectedBackup { get; private set; }

        public RollbackDialog(string installPath, IReadOnlyList<BackupInfo> backups)
        {
            _installPath = installPath ?? throw new ArgumentNullException(nameof(installPath));
            _backups = (backups ?? throw new ArgumentNullException(nameof(backups))).ToList();

            Text = "Rollback to a previous install";
            ClientSize = new Size(720, 440);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 320);
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            BuildLayout();
            PopulateBackupList();

            _backupList.SelectedIndexChanged += OnBackupSelected;
            _restoreButton.Click += OnRestoreClicked;
            CancelButton = _cancelButton;
        }

        private void BuildLayout()
        {
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(12),
            };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            outer.Controls.Add(new Label { Text = "Available backups:", AutoSize = true }, 0, 0);
            outer.Controls.Add(new Label { Text = "Files that will be restored:", AutoSize = true }, 1, 0);

            outer.Controls.Add(_backupList, 0, 1);
            outer.Controls.Add(_planPreview, 1, 1);

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0),
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(_summaryLabel, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
            };
            buttons.Controls.Add(_cancelButton);
            buttons.Controls.Add(_restoreButton);
            bottom.Controls.Add(buttons, 1, 0);

            outer.Controls.Add(bottom, 0, 2);
            outer.SetColumnSpan(bottom, 2);

            Controls.Add(outer);
        }

        private void PopulateBackupList()
        {
            _backupList.Items.Clear();
            foreach (var b in _backups)
            {
                var ts = b.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
                var label = $"{ts}  ({b.ReplacingTag ?? "unknown"})";
                if (b.InProgress) label += "  [interrupted]";
                _backupList.Items.Add(label);
            }
            if (_backupList.Items.Count > 0) _backupList.SelectedIndex = 0;
        }

        private void OnBackupSelected(object? sender, EventArgs e)
        {
            if (_backupList.SelectedIndex < 0 || _backupList.SelectedIndex >= _backups.Count)
            {
                _planPreview.Clear();
                _summaryLabel.Text = "";
                _restoreButton.Enabled = false;
                return;
            }

            var info = _backups[_backupList.SelectedIndex];

            // Cross-check: if the manifest's recorded install path differs
            // from the install we're rolling back into, surface the
            // mismatch. This catches the rare case where an install was
            // moved on disk while still hashing to the same bucket (e.g.
            // case-insensitive collision).
            if (!string.IsNullOrEmpty(info.ManifestInstallPath)
                && !string.Equals(
                    Path.GetFullPath(info.ManifestInstallPath),
                    Path.GetFullPath(_installPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                _summaryLabel.Text =
                    $"WARNING: this backup was made from '{info.ManifestInstallPath}', " +
                    $"not the currently-selected '{_installPath}'.";
            }
            else
            {
                _summaryLabel.Text = "";
            }

            try
            {
                var plan = BackupManager.PlanRestore(info.Path, _installPath);
                if (plan.Count == 0)
                {
                    _planPreview.Text = "(this backup has no files to restore)";
                    _restoreButton.Enabled = false;
                    return;
                }

                // Lay out one row per file: marker + relative path. Marker
                // is "(over)" if a current install-side file would be
                // overwritten, "(new) " if the install-side file is missing
                // (the operator-rare case where the backup includes a file
                // that's been deleted since the install). Fixed-width
                // marker makes the list easy to scan.
                var canonicalInstall = Path.GetFullPath(_installPath);
                var overCount = plan.Count(a => a.OverwritesExisting);
                var newCount = plan.Count - overCount;
                var lines = plan.Select(action =>
                {
                    var rel = Path.GetRelativePath(canonicalInstall, action.TargetInstallPath);
                    var marker = action.OverwritesExisting ? "(over)" : "(new) ";
                    return $"{marker}  {rel}";
                });
                _planPreview.Text = string.Join(Environment.NewLine, lines);
                // Append the per-action count summary to whatever
                // ManifestInstallPath warning the previous block may have
                // set. Both fit on one line for typical installs.
                var countSummary = $"Will restore {plan.Count} files ({overCount} overwrite, {newCount} new).";
                _summaryLabel.Text = string.IsNullOrEmpty(_summaryLabel.Text)
                    ? countSummary
                    : $"{_summaryLabel.Text}  |  {countSummary}";
                _restoreButton.Enabled = true;
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException
                or ArgumentException)
            {
                _planPreview.Text = $"Could not plan restore: {ex.GetType().Name}: {ex.Message}";
                _restoreButton.Enabled = false;
            }
        }

        private void OnRestoreClicked(object? sender, EventArgs e)
        {
            if (_backupList.SelectedIndex < 0 || _backupList.SelectedIndex >= _backups.Count) return;
            var info = _backups[_backupList.SelectedIndex];

            var confirm = MessageBox.Show(
                this,
                $"Restore {info.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} into\n  {_installPath}\n\n" +
                "Files in the install at the listed paths will be replaced with the backup copies. " +
                "If you have other backups in the list, you can roll forward again from those.\n\n" +
                "Note: a crash DURING the restore would leave the install in a mixed state " +
                "(restore is single-pass; it doesn't create its own backup of the in-place files).\n\n" +
                "Proceed?",
                "Confirm rollback",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            SelectedBackup = info;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
