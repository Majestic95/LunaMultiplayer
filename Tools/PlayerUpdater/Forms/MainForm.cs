using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LunaMultiplayer.PlayerUpdater.Core;

namespace LunaMultiplayer.PlayerUpdater.Forms
{
    // The single application window. Three logical states driven by the
    // _state field:
    //   1. Idle             — installed version visible; "Check for Update"
    //   2. UpdateAvailable  — installed + latest visible; "Install"
    //   3. Installed        — same labels; "Check Again" + Rollback if backups
    //
    // The form does NOT own destructive logic. Every long/destructive
    // operation is a one-call Core API (GitHubClient.GetReleasesForChannelAsync,
    // InstallPipeline.InstallAsync, BackupManager.RestoreBackup) — the form is
    // a state machine + Composition layer per the Core 5 commit's lesson
    // ("Core orchestrator pattern paid off"). All async work runs through
    // async-void button handlers; the awaited Task completes on the UI thread
    // because Progress<T> captures the form's SynchronizationContext.
    //
    // The GitHubClient is owned by the form for its lifetime so the in-process
    // 5-minute cache survives across "Check for Update" clicks. InstallPipeline
    // is constructed per-install because it owns an HttpDownloader.
    internal sealed class MainForm : Form
    {
        private enum State
        {
            // First load — no install detected, no version data yet. Buttons
            // mostly disabled.
            Initializing,
            // Idle state with detected install. Check button enabled.
            Idle,
            // GitHub fetch is in-flight. Buttons disabled.
            Checking,
            // Fetch returned a release with a higher (or different-channel)
            // version than installed. Install button enabled (subject to
            // pre-flight gates).
            UpdateAvailable,
            // Fetch returned no newer release.
            UpToDate,
            // Install just succeeded. Rollback button enabled.
            Installed,
        }

        // -- top-level layout --
        private readonly ComboBox _installDropdown = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button _browseButton = new() { Text = "Browse…", AutoSize = true };
        private readonly Label _installedLabel = new() { AutoSize = true, Text = "—" };
        private readonly ComboBox _channelDropdown = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _statusLabel = new() { AutoSize = true, MaximumSize = new Size(520, 0), Text = "" };
        private readonly TextBox _releaseNotesBox = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            WordWrap = true,
        };
        private readonly Button _checkButton = new() { Text = "Check for Update", AutoSize = true };
        private readonly Button _installButton = new() { Text = "Install", AutoSize = true, Enabled = false };
        private readonly Button _rollbackButton = new() { Text = "Rollback…", AutoSize = true, Enabled = false };

        // -- owned by the form --
        private readonly GitHubClient _gitHubClient = new();

        // -- mutable state --
        private State _state = State.Initializing;
        private UpdaterSettings _settings = UpdaterSettings.Default;
        private VersionMetadata? _installedVersion;
        // Tri-state install-detection: true means the .version file was
        // present but unreadable (corrupt JSON, IO failure). Distinguishes
        // "no LMP installed" (false + null _installedVersion) from "LMP IS
        // installed but we don't know which version" — the cross-channel
        // pre-flight needs this distinction to avoid silently letting the
        // operator clobber an existing install of a different cohort with
        // no warning (upgrade-lens MUST FIX #2).
        private bool _installedVersionUnreadable;
        private GitHubRelease? _candidateRelease;
        private GitHubAsset? _candidateAsset;

        // De-dupe set for the "previous install was interrupted" rescue
        // prompt. Once the operator has been offered a particular backup
        // (whether they accepted or declined), we don't re-prompt for that
        // exact backup again this session. Re-prompting fires fresh on
        // every install-dropdown change / Browse pick / channel switch
        // per upgrade-lens MUST FIX #1.
        private readonly HashSet<string> _rescueOffered = new(StringComparer.OrdinalIgnoreCase);

        // Cached presence-of-backups result keyed by install path. The
        // RefreshButtons hot path used to call BackupManager.ListBackups
        // on every state transition — a synchronous disk walk that could
        // hang the UI thread on an offline drive (consumer-lens MUST FIX
        // #2). Cache is invalidated explicitly post-install / post-rollback
        // / on install change.
        private readonly Dictionary<string, bool> _hasBackupsCache = new(StringComparer.OrdinalIgnoreCase);

        // Suppresses the SelectedIndexChanged handlers while we programmatically
        // mutate the dropdowns. Without this, populating the channel dropdown
        // during state transitions re-fires the channel-change handler and we
        // get a double Check pass.
        private bool _suppressDropdownEvents;

        public MainForm()
        {
            Text = "Luna Multiplayer Player Updater";
            ClientSize = new Size(580, 480);
            MinimumSize = new Size(560, 460);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;

            BuildLayout();

            _installDropdown.SelectedIndexChanged += OnInstallChanged;
            _browseButton.Click += OnBrowseClicked;
            _channelDropdown.SelectedIndexChanged += OnChannelChanged;
            _checkButton.Click += OnCheckClicked;
            _installButton.Click += OnInstallClicked;
            _rollbackButton.Click += OnRollbackClicked;

            Load += OnFormLoad;
            FormClosing += OnFormClosing;
        }

        private void BuildLayout()
        {
            // Two-column outer grid. Left column is the labels, right column
            // is the controls. Row heights AutoSize so labels fit; the
            // release-notes row uses Percent so it stretches to fill.
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(12),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 5; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Row 0: install dropdown + browse button
            grid.Controls.Add(new Label { Text = "KSP install:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            var installRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0),
            };
            installRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            installRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            installRow.Controls.Add(_installDropdown, 0, 0);
            installRow.Controls.Add(_browseButton, 1, 0);
            grid.Controls.Add(installRow, 1, 0);

            // Row 1: installed version
            grid.Controls.Add(new Label { Text = "Installed:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            grid.Controls.Add(_installedLabel, 1, 1);

            // Row 2: channel dropdown
            grid.Controls.Add(new Label { Text = "Channel:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            grid.Controls.Add(_channelDropdown, 1, 2);

            // Row 3: status label spans both columns
            var statusHeader = new Label { Text = "Status:", AutoSize = true, Anchor = AnchorStyles.Left };
            grid.Controls.Add(statusHeader, 0, 3);
            grid.Controls.Add(_statusLabel, 1, 3);

            // Row 4: release-notes box header
            grid.Controls.Add(new Label { Text = "Release notes:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
            // (filler — same row as the header but right column gets nothing)
            grid.Controls.Add(new Label { Text = "", AutoSize = true }, 1, 4);

            // Row 5: release-notes textbox spans both columns (we add at col 0
            // with column-span 2)
            grid.Controls.Add(_releaseNotesBox, 0, 5);
            grid.SetColumnSpan(_releaseNotesBox, 2);

            // Row 6: button bar, both columns
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0),
            };
            buttons.Controls.Add(_installButton);
            buttons.Controls.Add(_checkButton);
            buttons.Controls.Add(_rollbackButton);
            grid.Controls.Add(buttons, 0, 6);
            grid.SetColumnSpan(buttons, 2);

            Controls.Add(grid);
        }

        // -- form lifecycle --

        private void OnFormLoad(object? sender, EventArgs e)
        {
            // Best-effort cleanup of staging zips orphaned by a previous
            // process kill. Default minAgeHours=24 means we won't race a
            // concurrent install that just started.
            try { InstallPipeline.SweepOrphanedStagingFiles(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swallow */ }

            _settings = SettingsStore.ReadSettings();

            // Detect KSP candidates. EnumerateCandidates yields lastUsedPath
            // first if it still validates, then Steam → CKAN → GOG.
            var candidates = KspDetector.EnumerateCandidates(_settings.LastKspPath).ToList();

            _suppressDropdownEvents = true;
            try
            {
                _installDropdown.Items.Clear();
                foreach (var path in candidates) _installDropdown.Items.Add(path);

                _channelDropdown.Items.Clear();
                _channelDropdown.Items.Add(VersionMetadata.ChannelStable);
                _channelDropdown.Items.Add(VersionMetadata.ChannelPrivate);
                _channelDropdown.Items.Add(VersionMetadata.ChannelPerAgencyPrivate);

                if (_installDropdown.Items.Count > 0)
                {
                    _installDropdown.SelectedIndex = 0;
                }
            }
            finally
            {
                _suppressDropdownEvents = false;
            }

            if (_installDropdown.Items.Count == 0)
            {
                // No detected install — operator must browse.
                _state = State.Initializing;
                SetStatus(
                    "No KSP install detected via Steam, CKAN, or GOG.\n" +
                    "Click Browse… to pick the KSP root manually (the folder containing GameData/Squad).");
                RefreshButtons();
                return;
            }

            RefreshFromCurrentInstall();
            // Rescue prompt now fires from RefreshFromCurrentInstall so an
            // operator Browsing to a different install (or whose initial
            // candidate had no interrupt but a later-Browsed install does)
            // sees the prompt for THAT install. See HashSet de-dupe in
            // OfferInterruptedInstallRescueIfAny.
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            // Persist preferences so next launch picks the same install +
            // channel. Failure is a one-session annoyance.
            //
            // Null-guard on LastKspPath: if the operator launched and
            // closed without picking ANY install (e.g. cancelled the
            // Browse dialog after auto-detect found nothing), the dropdown
            // SelectedItem is null. Without the guard we'd write
            // LastKspPath=null and blow away the breadcrumb from a
            // previous session where the operator HAD picked an install.
            // Preserve the prior value in that case.
            var path = _installDropdown.SelectedItem as string ?? _settings.LastKspPath;
            var channel = _channelDropdown.SelectedItem as string ?? _settings.LastChannelPreference;
            var retention = _settings.BackupRetention <= 0
                ? UpdaterSettings.DefaultBackupRetention
                : _settings.BackupRetention;
            SettingsStore.WriteSettings(new UpdaterSettings(path, channel, retention));

            _gitHubClient.Dispose();
        }

        // -- handlers --

        private void OnInstallChanged(object? sender, EventArgs e)
        {
            if (_suppressDropdownEvents) return;
            RefreshFromCurrentInstall();
        }

        private void OnChannelChanged(object? sender, EventArgs e)
        {
            if (_suppressDropdownEvents) return;
            // Channel changed without a re-check; clear any stale "update
            // available" state from the previous channel selection.
            _candidateRelease = null;
            _candidateAsset = null;
            _releaseNotesBox.Clear();
            _state = State.Idle;
            // Hint at cohort mismatch when the selected channel differs
            // from the install's current channel — gives the operator
            // advance warning before they Check + see the pre-flight
            // cross-channel confirm dialog.
            var selectedChannel = _channelDropdown.SelectedItem as string;
            if (_installedVersion != null
                && !string.IsNullOrEmpty(selectedChannel)
                && !string.Equals(_installedVersion.Channel, selectedChannel, StringComparison.Ordinal))
            {
                SetStatus($"This install is on '{_installedVersion.Channel}'. Switching to '{selectedChannel}' " +
                    "will be a cross-channel install (cohort mismatch). Click Check for Update to proceed.");
            }
            else
            {
                SetStatus("Click Check for Update to query the selected channel.");
            }
            RefreshButtons();
        }

        private void OnBrowseClicked(object? sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Pick the KSP install root (the folder containing GameData/Squad).",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var picked = dialog.SelectedPath;
            if (!KspDetector.ValidateKspPath(picked))
            {
                MessageBox.Show(
                    this,
                    $"That folder does not look like a KSP install (no GameData/Squad subdirectory found):\n\n{picked}",
                    "Not a KSP install",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _suppressDropdownEvents = true;
            try
            {
                if (!_installDropdown.Items.Cast<string>().Any(p => string.Equals(p, picked, StringComparison.OrdinalIgnoreCase)))
                {
                    _installDropdown.Items.Add(picked);
                }
                _installDropdown.SelectedItem = _installDropdown.Items.Cast<string>()
                    .First(p => string.Equals(p, picked, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressDropdownEvents = false;
            }
            // Persist the freshly-picked path immediately so an unexpected
            // exit before OnFormClosing still preserves the breadcrumb on
            // the next launch (consumer-lens SHOULD FIX #11).
            _settings = _settings with { LastKspPath = picked };
            SettingsStore.WriteSettings(_settings);
            RefreshFromCurrentInstall();
        }

        private async void OnCheckClicked(object? sender, EventArgs e)
        {
            var installPath = _installDropdown.SelectedItem as string;
            var channel = _channelDropdown.SelectedItem as string;
            if (string.IsNullOrEmpty(installPath) || string.IsNullOrEmpty(channel)) return;

            // Capture _installedVersion BEFORE the await so a mid-check
            // dropdown change (operator switching install paths) doesn't
            // misalign the version comparison when the await resumes
            // (consumer-lens SHOULD FIX #5). Even if the operator switches
            // install dropdowns during the fetch, the check's verdict
            // remains consistent with what they saw when they clicked.
            var capturedInstalled = _installedVersion;

            _state = State.Checking;
            SetStatus($"Querying GitHub for the latest '{channel}' release…");
            // Disable dropdowns + Browse while the check is in flight so
            // the operator can't reset the captured state mid-await
            // (consumer-lens SHOULD FIX #6). RefreshButtons re-enables
            // them on every code path below.
            SetInputsEnabled(false);
            RefreshButtons();

            try
            {
                _gitHubClient.InvalidateCache();
                var releases = await _gitHubClient.GetReleasesForChannelAsync(channel).ConfigureAwait(true);
                var latest = SelectLatestPlayableRelease(releases);
                if (latest == null)
                {
                    _state = State.UpToDate;
                    SetStatus($"No '{channel}'-channel releases found on Majestic95/LunaMultiplayer with a playable Client asset.");
                    _releaseNotesBox.Clear();
                    return;
                }

                // "Newer than installed" decision: an install on a different
                // channel always counts as an update opportunity (operator
                // is opting in to a different cohort line). Same channel:
                // compare ordinals. Compare against the captured snapshot.
                var isDifferent = capturedInstalled == null
                    || !string.Equals(capturedInstalled.Channel, latest.Release.Version.Channel, StringComparison.Ordinal)
                    || CompareVersions(latest.Release.Version, capturedInstalled) > 0;

                if (!isDifferent)
                {
                    _state = State.UpToDate;
                    SetStatus($"You're on the latest '{channel}' release: {capturedInstalled!.Tag}.");
                    _releaseNotesBox.Clear();
                    _candidateRelease = null;
                    _candidateAsset = null;
                    return;
                }

                _candidateRelease = latest.Release;
                _candidateAsset = latest.Asset;
                _releaseNotesBox.Text = string.IsNullOrWhiteSpace(latest.Release.Body)
                    ? "(no release notes published)"
                    : NormaliseLineEndings(latest.Release.Body);
                _state = State.UpdateAvailable;
                var installedDescription = capturedInstalled?.Tag ?? "(no LMP installed)";
                var sizeHint = $"{FormatBytes(latest.Asset.Size)} download, ~{FormatBytes(latest.Asset.Size * 3)} free space required";
                SetStatus($"Update available: {installedDescription} → {latest.Release.Tag}\nAsset: {latest.Asset.Name} ({sizeHint})");
            }
            catch (GitHubRateLimitException ex)
            {
                _state = State.Idle;
                var resetText = ex.ResetAt > DateTimeOffset.MinValue
                    ? ex.ResetAt.ToLocalTime().ToString("HH:mm 'on' yyyy-MM-dd", CultureInfo.CurrentCulture)
                    : "an unknown time";
                SetStatus($"GitHub rate limit reached. Try again at {resetText}.");
                _releaseNotesBox.Clear();
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or System.Text.Json.JsonException
                or IOException
                or TaskCanceledException)
            {
                _state = State.Idle;
                SetStatus($"Could not reach GitHub: {ex.Message}");
                // Clear stale notes from a previous successful check so the
                // operator doesn't think the failed check returned those
                // notes (consumer-lens SHOULD FIX #12).
                _releaseNotesBox.Clear();
            }
            finally
            {
                SetInputsEnabled(true);
                RefreshButtons();
            }
        }

        // Toggles install/channel dropdowns + Browse + Rollback during
        // long-running operations (Checking, Installing). Check + Install
        // buttons are gated by RefreshButtons on state semantics; this
        // covers the controls that would otherwise let the operator
        // mutate the captured state mid-await.
        private void SetInputsEnabled(bool enabled)
        {
            _installDropdown.Enabled = enabled;
            _channelDropdown.Enabled = enabled;
            _browseButton.Enabled = enabled;
            // Check/Install/Rollback gating handled by RefreshButtons.
        }

        private async void OnInstallClicked(object? sender, EventArgs e)
        {
            if (_candidateRelease == null || _candidateAsset == null) return;
            var installPath = _installDropdown.SelectedItem as string;
            if (string.IsNullOrEmpty(installPath))
            {
                MessageBox.Show(this, "No KSP install selected.", "Cannot install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Pre-flight 1: KSP-running check. Refuse the install if KSP is
            // open; warn-and-confirm on Unknown so a locked-down OS doesn't
            // strand the player.
            var kspState = KspRunningCheck.ProbeKspRunningState();
            if (kspState == KspRunningCheck.ProbeState.Running)
            {
                MessageBox.Show(
                    this,
                    "Kerbal Space Program is currently running.\n\n" +
                    "Close KSP completely (including the launcher) and try again.",
                    "KSP is running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            if (kspState == KspRunningCheck.ProbeState.Unknown)
            {
                var proceed = MessageBox.Show(
                    this,
                    "Could not determine whether KSP is running (the OS refused to enumerate processes).\n\n" +
                    "If KSP is open, the install will fail with a file-sharing error.\n\n" +
                    "Proceed with install anyway?",
                    "KSP-running state unknown",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (proceed != DialogResult.Yes) return;
            }

            // Pre-flight 2: disk-space check. Refuse on Insufficient; warn-
            // and-proceed on Unknown.
            var spaceResult = DiskSpaceCheck.Check(installPath, _candidateAsset.Size);
            if (spaceResult.Outcome == DiskSpaceCheck.Outcome.Insufficient)
            {
                MessageBox.Show(
                    this,
                    $"Drive {spaceResult.DriveRoot} does not have enough free space.\n\n" +
                    $"Available: {FormatBytes(spaceResult.AvailableBytes)}\n" +
                    $"Required:  {FormatBytes(spaceResult.RequiredBytes)} (3× the zip size for download + extract + backup)\n\n" +
                    "Free up space and retry.",
                    "Insufficient disk space",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Pre-flight 3: probe-write detects a read-only install (typically
            // Program Files). Refuse with operator-recovery guidance — never
            // request UAC elevation. Locked design decision per the project
            // memory: asInvoker manifest, no silent UAC.
            if (!ProbeWritable(installPath))
            {
                MessageBox.Show(
                    this,
                    $"The KSP install at\n\n  {installPath}\n\nis read-only for the current user account " +
                    "(typically because it's under Program Files).\n\n" +
                    "Either:\n" +
                    "  • Move the KSP install to a writable folder (e.g. C:\\Games\\KSP), or\n" +
                    "  • Run the PlayerUpdater as Administrator (right-click → Run as administrator).\n\n" +
                    "The updater itself does NOT request elevation so a permission misconfiguration " +
                    "is surfaced explicitly instead of silently bypassed.",
                    "KSP install is not writable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Pre-flight 4: cross-channel switch confirmation. Locked design
            // decision per project memory — switching channels is a
            // protocol-incompatibility risk so the operator must opt in
            // explicitly.
            //
            // Three install-detection states feed this gate:
            //   (a) version known & channel differs → warn explicitly
            //   (b) version present-but-unreadable → warn that the
            //       existing install may be a different cohort
            //       (upgrade-lens MUST FIX #2 — closes the TOCTOU hole
            //       where a corrupt .version file silently bypassed the
            //       warning)
            //   (c) no version & no file at all → fresh install, no
            //       warning needed
            if (_installedVersion != null
                && !string.Equals(_installedVersion.Channel, _candidateRelease.Version.Channel, StringComparison.Ordinal))
            {
                var confirm = MessageBox.Show(
                    this,
                    $"You're installing a '{_candidateRelease.Version.Channel}' release on top of a " +
                    $"'{_installedVersion.Channel}' install.\n\n" +
                    "Different channels can have incompatible network protocols; you will not be able to " +
                    "play with friends on the other channel's servers until everyone matches.\n\n" +
                    "Continue with the channel switch?",
                    "Cross-channel switch",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes) return;
            }
            else if (_installedVersionUnreadable)
            {
                var confirm = MessageBox.Show(
                    this,
                    "LunaMultiplayer.version exists at this install but could not be parsed, " +
                    "so the existing cohort is unknown.\n\n" +
                    $"You're about to install a '{_candidateRelease.Version.Channel}' release. " +
                    "If the existing install was a DIFFERENT channel (stable / private / per-agency-private), " +
                    "you'll be switching cohorts and will not be able to play with friends on the previous " +
                    "channel's servers until everyone matches.\n\n" +
                    "Continue?",
                    "Existing install — cohort unknown",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes) return;
            }

            // Pre-flight 5: HashSkipped pre-confirmation. The InstallPipeline
            // contract leaves it to Forms to decide whether to install a
            // release whose asset has no SHA-256 digest. We confirm BEFORE
            // launching the install rather than after success, so the
            // operator opts in knowingly (consumer-lens MUST FIX #4).
            if (_candidateAsset.Sha256Hex == null)
            {
                var confirm = MessageBox.Show(
                    this,
                    $"The release asset '{_candidateAsset.Name}' does not publish a SHA-256 hash on GitHub.\n\n" +
                    "This is normal for older releases that predate the hash-verification rollout (October 2024). " +
                    "The download will be installed without integrity verification — if GitHub's CDN serves a " +
                    "corrupted byte, we won't catch it.\n\n" +
                    "Continue without hash verification?",
                    "No hash available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
            }

            // All pre-flight checks passed. Hand off to ProgressForm which
            // owns the InstallPipeline + cancellation surface for the
            // duration of the install.
            //
            // ReplacingTag: pass through null when no installed version is
            // known so the backup manifest stores a clean null rather than
            // the literal sentinel string "(unknown)" — RollbackDialog
            // renders missing tags as "unknown previous version" itself.
            var request = new InstallRequest(
                installPath,
                _candidateAsset,
                ReplacingTag: _installedVersion?.Tag ?? string.Empty,
                BackupRetention: _settings.BackupRetention <= 0
                    ? UpdaterSettings.DefaultBackupRetention
                    : _settings.BackupRetention);

            InstallResult? result;
            using (var progress = new ProgressForm(request))
            {
                progress.ShowDialog(this);
                result = progress.Result;
            }

            // Whatever the outcome, the cached backup-presence answer for
            // this install is stale (BackingUp stage may have created a
            // new backup dir even on cancel).
            InvalidateBackupsCache(installPath);

            if (result == null)
            {
                // ProgressForm closed without a result (cancellation).
                //
                // Mid-extract cancel leaves the install in a MIXED state
                // (partial overlay over original files). The Extracting
                // stage is where a cancel is dangerous; Downloading +
                // Verifying + BackingUp are pre-mutation. Without a hint
                // here the operator may close the app and launch a broken
                // KSP. Offer rollback immediately for any cancel that
                // leaves backupDir set — the rescue prompt on next launch
                // is the belt; this is the braces (consumer-lens SHOULD
                // FIX #7). _rescueOffered de-dupe still applies on the
                // next launch, so the operator won't see double prompts.
                _state = State.Idle;
                SetStatus("Install cancelled.");
                var inProgress = BackupManager.ListBackups(installPath)
                    .Where(b => b.InProgress)
                    .OrderByDescending(b => b.Timestamp)
                    .FirstOrDefault();
                if (inProgress != null)
                {
                    var confirm = MessageBox.Show(
                        this,
                        "The install was cancelled. If cancellation happened during the Extracting stage, " +
                        "the KSP install may be in a mixed state (some new files, some old).\n\n" +
                        "Roll back to the pre-install state now?",
                        "Install cancelled — rollback?",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (confirm == DialogResult.Yes)
                    {
                        RestoreFromBackup(installPath, inProgress.Path);
                        return;
                    }
                    // Operator declined immediate rollback — mark this
                    // specific backup as already-offered so the next-launch
                    // rescue prompt doesn't re-pester them.
                    _rescueOffered.Add(inProgress.Path);
                }
                RefreshFromCurrentInstall();
                return;
            }

            if (result.Outcome == InstallOutcome.Success)
            {
                _state = State.Installed;
                SetStatus($"Installed {_candidateRelease.Tag} successfully.");
                _candidateRelease = null;
                _candidateAsset = null;
                _releaseNotesBox.Clear();
                RefreshFromCurrentInstall();
                return;
            }

            // Refusal. Build error text including the per-outcome explanation
            // + the raw Error string from the pipeline (often the most useful
            // diagnostic).
            using (var dialog = new ErrorDialog(
                title: FormatOutcomeTitle(result.Outcome),
                summary: FormatOutcomeSummary(result.Outcome),
                details: result.Error ?? "(no diagnostic)",
                offerRollback: result.BackupDir != null))
            {
                var dlgResult = dialog.ShowDialog(this);
                if (dlgResult == DialogResult.Retry && result.BackupDir != null)
                {
                    RestoreFromBackup(installPath, result.BackupDir);
                    return;
                }
            }

            _state = State.Idle;
            RefreshFromCurrentInstall();
        }

        private void OnRollbackClicked(object? sender, EventArgs e)
        {
            var installPath = _installDropdown.SelectedItem as string;
            if (string.IsNullOrEmpty(installPath)) return;

            var backups = BackupManager.ListBackups(installPath).ToList();
            if (backups.Count == 0)
            {
                MessageBox.Show(this, "No backups available for this install.", "Nothing to roll back", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dialog = new RollbackDialog(installPath, backups);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedBackup == null) return;
            RestoreFromBackup(installPath, dialog.SelectedBackup.Path);
        }

        // -- helpers --

        private void RestoreFromBackup(string installPath, string backupDir)
        {
            // Mark this backup as already-handled BEFORE the restore call
            // so a partial restore + subsequent install-change doesn't
            // re-offer the rescue prompt for the same path.
            _rescueOffered.Add(backupDir);
            try
            {
                var restored = BackupManager.RestoreBackup(backupDir, installPath);
                _state = State.Idle;
                SetStatus($"Restored {restored} files from backup at {backupDir}.");
                InvalidateBackupsCache(installPath);
                RefreshFromCurrentInstall();
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException
                or ArgumentException)
            {
                using var dialog = new ErrorDialog(
                    title: "Rollback failed",
                    summary: "The backup could not be fully restored. The KSP install may be in a mixed state.",
                    details: $"{ex.GetType().Name}: {ex.Message}",
                    offerRollback: false);
                dialog.ShowDialog(this);
                _state = State.Idle;
                InvalidateBackupsCache(installPath);
                RefreshFromCurrentInstall();
            }
        }

        private void RefreshFromCurrentInstall()
        {
            var installPath = _installDropdown.SelectedItem as string;
            if (string.IsNullOrEmpty(installPath))
            {
                _installedLabel.Text = "—";
                _installedVersion = null;
                _installedVersionUnreadable = false;
                _state = State.Initializing;
                RefreshButtons();
                return;
            }

            // Cache invalidation is keyed on install path — switching to a
            // different install means re-probing for backups.
            InvalidateBackupsCache(installPath);

            _installedVersion = VersionFileReader.ReadInstalledVersion(installPath);

            // Distinguish "no LMP installed" from "LMP installed but
            // version file is corrupt or unreadable." The latter case is
            // load-bearing for the cross-channel pre-flight (we can't
            // skip the cohort-mismatch warning just because the version
            // string can't be parsed).
            var versionFilePath = Path.Combine(installPath,
                VersionFileReader.RelativeVersionPath.Replace('/', Path.DirectorySeparatorChar));
            _installedVersionUnreadable = _installedVersion == null && File.Exists(versionFilePath);

            if (_installedVersion == null)
            {
                if (_installedVersionUnreadable)
                {
                    _installedLabel.Text = "(version file present but unreadable — cohort unknown)";
                    _state = State.Idle;
                    SetStatus("LunaMultiplayer.version exists at this install but could not be parsed. " +
                        "Any install proceeding from here will warn about possible cohort mismatch.");
                }
                else
                {
                    _installedLabel.Text = $"(no LunaMultiplayer.version file in {installPath})";
                    _state = State.Idle;
                    SetStatus("This install does not have LMP installed yet, or the .version file is missing. " +
                        "Pick a channel and click Check for Update to install fresh.");
                }
            }
            else
            {
                _installedLabel.Text = $"{_installedVersion.Tag}";
                _state = State.Idle;
                SetStatus("Click Check for Update to look for newer releases on the selected channel.");
            }

            // Default channel selection mirrors detected install if known,
            // else lastChannelPreference, else stable.
            _suppressDropdownEvents = true;
            try
            {
                var channel = _installedVersion?.Channel
                    ?? _settings.LastChannelPreference
                    ?? VersionMetadata.ChannelStable;
                // Treat 'dev' as stable for dropdown selection — dev tags
                // don't correspond to a published channel.
                if (string.Equals(channel, VersionMetadata.ChannelDev, StringComparison.Ordinal))
                {
                    channel = VersionMetadata.ChannelStable;
                }
                var idx = _channelDropdown.Items.IndexOf(channel);
                _channelDropdown.SelectedIndex = idx >= 0 ? idx : 0;
            }
            finally
            {
                _suppressDropdownEvents = false;
            }

            RefreshButtons();
            // Fire the interrupted-install rescue prompt AFTER the buttons
            // settle. Re-runs on every install change (per upgrade-lens
            // MUST FIX #1); de-duped via _rescueOffered so the operator
            // doesn't see the same prompt twice in one session.
            OfferInterruptedInstallRescueIfAny();
        }

        private void OfferInterruptedInstallRescueIfAny()
        {
            var installPath = _installDropdown.SelectedItem as string;
            if (string.IsNullOrEmpty(installPath)) return;

            List<BackupInfo> inProgress;
            try
            {
                inProgress = BackupManager.ListBackups(installPath)
                    .Where(b => b.InProgress)
                    .OrderByDescending(b => b.Timestamp)
                    .ToList();
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                // Drive offline / locked-down / malformed path. Don't
                // surface the failure here — RefreshButtons will gate
                // Rollback off the same data, and the operator can pick
                // a different install via Browse.
                return;
            }
            if (inProgress.Count == 0) return;

            var newest = inProgress[0];
            // De-dupe: same backup path → don't re-prompt this session.
            // The operator already saw + acted on (or dismissed) this
            // specific interrupted-install signal. Re-prompting fires
            // only when a DIFFERENT interrupted backup surfaces (e.g.
            // operator Browsed to a different install).
            if (!_rescueOffered.Add(newest.Path)) return;

            var replacing = newest.ReplacingTag ?? "(unknown previous version)";
            var ts = newest.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            var result = MessageBox.Show(
                this,
                $"A previous install on {ts} was interrupted before it could complete.\n\n" +
                $"The KSP install at this path may be in a mixed state. A backup of the files " +
                $"that would have been overwritten by '{replacing}' is still on disk.\n\n" +
                "Restore from that backup now?",
                "Previous install was interrupted",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                RestoreFromBackup(installPath, newest.Path);
            }
        }

        private void RefreshButtons()
        {
            var hasInstall = _installDropdown.SelectedItem is string;
            _checkButton.Enabled = hasInstall && _state != State.Checking;
            _installButton.Enabled = hasInstall
                && _state == State.UpdateAvailable
                && _candidateRelease != null
                && _candidateAsset != null;
            _rollbackButton.Enabled = hasInstall && HasAnyBackups();
        }

        private bool HasAnyBackups()
        {
            var installPath = _installDropdown.SelectedItem as string;
            if (string.IsNullOrEmpty(installPath)) return false;
            if (_hasBackupsCache.TryGetValue(installPath, out var cached)) return cached;
            try
            {
                var any = BackupManager.ListBackups(installPath).Count > 0;
                _hasBackupsCache[installPath] = any;
                return any;
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                _hasBackupsCache[installPath] = false;
                return false;
            }
        }

        // Invalidates the HasAnyBackups cache for a specific install (or
        // all installs if path is null). Called after every install /
        // rollback / install-dropdown change so RefreshButtons doesn't
        // serve a stale answer.
        private void InvalidateBackupsCache(string? installPath)
        {
            if (installPath == null) _hasBackupsCache.Clear();
            else _hasBackupsCache.Remove(installPath);
        }

        private void SetStatus(string text) => _statusLabel.Text = text;

        // Probes whether the install dir is writable by the current process
        // by creating a small zero-byte file at a uniquely-named path and
        // deleting it. Avoids relying on UAC-aware DACL parsing — the operator
        // is asInvoker, so "write succeeds" is the same surface the install
        // pipeline will hit.
        private static bool ProbeWritable(string installPath)
        {
            var probe = Path.Combine(installPath, $".lmp-playerupdater-write-probe-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                return false;
            }
        }

        // Picks the latest release matching the channel that has a usable
        // -Client-* asset. We skip the *-AdminGui-* and *-MasterServer-* +
        // server-side assets — the in-game updater is for players.
        // Self-contained is preferred when present (no .NET 10 prerequisite
        // on the player machine).
        private static LatestRelease? SelectLatestPlayableRelease(IReadOnlyList<GitHubRelease> releases)
        {
            foreach (var release in releases)
            {
                var asset = SelectClientAsset(release);
                if (asset != null) return new LatestRelease(release, asset);
            }
            return null;
        }

        private static GitHubAsset? SelectClientAsset(GitHubRelease release)
        {
            // Pick the LunaMultiplayer-Client-Release.zip (the playable
            // client bundle). Other assets in the same release are server
            // / AdminGui / PlayerUpdater itself — players don't want those.
            return release.Assets.FirstOrDefault(a =>
                a.Name.IndexOf("Client-Release", StringComparison.OrdinalIgnoreCase) >= 0
                && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        private sealed record LatestRelease(GitHubRelease Release, GitHubAsset Asset);

        // Lexicographic comparison on (Major, Minor, Patch, Revision, Hotfix).
        // **Channel comparison is the CALLER's responsibility** — this method
        // ignores Channel entirely. Callers MUST check Channel equality
        // BEFORE deciding whether a CompareVersions result is meaningful
        // (a higher Major.Minor.Patch on the wrong channel is not "newer"
        // for the operator's cohort). See OnCheckClicked where we treat a
        // channel mismatch as "different" via an explicit first-pass before
        // falling through to this comparator. Null Revision/Hotfix treated
        // as 0 (matches spec ordering: '-8' and absent-revision are
        // ordered together; hotfix-zero is rejected at parse time so this
        // coalesce is safe).
        private static int CompareVersions(VersionMetadata a, VersionMetadata b)
        {
            var c = a.Major.CompareTo(b.Major);
            if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor);
            if (c != 0) return c;
            c = a.Patch.CompareTo(b.Patch);
            if (c != 0) return c;
            c = (a.Revision ?? 0).CompareTo(b.Revision ?? 0);
            if (c != 0) return c;
            return (a.Hotfix ?? 0).CompareTo(b.Hotfix ?? 0);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static string NormaliseLineEndings(string text)
        {
            // GitHub release bodies use \n; WinForms TextBox needs \r\n for
            // multiline rendering.
            return text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        private static string FormatOutcomeTitle(InstallOutcome outcome) => outcome switch
        {
            InstallOutcome.DiskSpaceInsufficient => "Not enough disk space",
            InstallOutcome.DownloadFailed => "Download failed",
            InstallOutcome.HashMismatch => "Hash mismatch",
            InstallOutcome.ExtractFailed => "Install failed",
            _ => "Install failed",
        };

        private static string FormatOutcomeSummary(InstallOutcome outcome) => outcome switch
        {
            InstallOutcome.DiskSpaceInsufficient =>
                "The install drive does not have enough free space for the download + extract + backup.",
            InstallOutcome.DownloadFailed =>
                "The release zip could not be downloaded from GitHub. The install is unchanged.",
            InstallOutcome.HashMismatch =>
                "The downloaded zip did not match GitHub's published SHA-256. The install is unchanged.",
            InstallOutcome.ExtractFailed =>
                "The extract step failed partway through. The KSP install may be in a mixed state — Rollback is recommended.",
            _ => "The install did not complete cleanly.",
        };
    }
}
