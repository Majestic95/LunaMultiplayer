using System;
using System.IO;
using System.Text.Json;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Persists the player's PlayerUpdater preferences across runs at
    // %LOCALAPPDATA%/LunaMultiplayer/PlayerUpdater/settings.json. The file
    // is OPTIONAL — every getter falls back to a sensible default on first
    // launch or when the file is corrupt. The updater must never fail to
    // start because of a settings-read failure.
    //
    // Crash safety: writes go to settings.json.tmp first, then File.Move
    // (overwrite: true) onto the canonical name. On NTFS this lowers to
    // MoveFileEx + MOVEFILE_REPLACE_EXISTING — best-effort atomic when no
    // concurrent reader holds a handle. A crash mid-write at worst leaves a
    // stale .tmp on disk. The read path recovers from one specific failure
    // mode: canonical file absent BUT .tmp present (rare — power loss
    // between WriteAllText and Move). Otherwise a corrupt or absent file
    // returns Default; we do not maintain a .bak generation.
    //
    // Reader/writer race contract: a concurrent ReadSettings while
    // WriteSettings is mid-rename may observe a sharing violation and
    // return Default. The consumer cannot rely on "I just wrote X, the next
    // read returns X" without external synchronisation. In the updater
    // this never matters because (a) the Mutex enforces single-instance and
    // (b) the UI is single-threaded — every read precedes every write on
    // the same thread.
    //
    // No locking — the updater is a single-instance app (enforced via the
    // Mutex in Program.cs), so concurrent writes within one user session
    // cannot happen. Two users on the same Windows machine writing
    // simultaneously is structurally impossible because %LOCALAPPDATA% is
    // per-user.
    public sealed record UpdaterSettings(
        string? LastKspPath,
        string? LastChannelPreference,
        int BackupRetention)
    {
        public const int DefaultBackupRetention = 3;

        // The "factory defaults" instance returned on first-launch or after
        // a read failure. Treat as immutable.
        public static UpdaterSettings Default { get; } =
            new(LastKspPath: null, LastChannelPreference: null, BackupRetention: DefaultBackupRetention);
    }

    public static class SettingsStore
    {
        // Path of the settings file relative to %LOCALAPPDATA%. Forward
        // slashes get rewritten to the OS separator at use time.
        public const string RelativeSettingsPath = "LunaMultiplayer/PlayerUpdater/settings.json";

        // Returns the absolute path the settings file would live at, or null
        // if the LocalApplicationData folder cannot be resolved (extremely
        // rare — would mean a profile-less Windows install).
        public static string? ResolveSettingsFilePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) return null;

            // Consume RelativeSettingsPath rather than re-building the path
            // here, so a change to the constant is reflected in the read.
            return Path.Combine(localAppData, RelativeSettingsPath.Replace('/', Path.DirectorySeparatorChar));
        }

        // Reads the settings file. Returns UpdaterSettings.Default if the
        // file is absent, unreadable, or malformed — never throws.
        public static UpdaterSettings ReadSettings()
        {
            var path = ResolveSettingsFilePath();
            if (path == null) return UpdaterSettings.Default;

            return ReadSettingsFromPath(path);
        }

        // Writes the settings file atomically. Returns true on success, false
        // on any I/O or serialization failure — callers should ignore failure
        // because losing the preference is a one-session annoyance, not a
        // showstopper.
        public static bool WriteSettings(UpdaterSettings settings)
        {
            if (settings == null) return false;

            var path = ResolveSettingsFilePath();
            if (path == null) return false;

            return WriteSettingsToPath(settings, path);
        }

        // Internal seam for testing — points at an arbitrary path so test
        // cases can use a temp directory without touching %LOCALAPPDATA%.
        internal static UpdaterSettings ReadSettingsFromPath(string path)
        {
            string json;
            try
            {
                if (File.Exists(path))
                {
                    json = File.ReadAllText(path);
                }
                else
                {
                    // Canonical missing — try the .tmp from a partially-completed
                    // previous write (WriteAllText succeeded, File.Move did not).
                    // The .tmp is the freshest data we have; the operator would
                    // rather get it back than start over.
                    var tmpPath = path + ".tmp";
                    if (!File.Exists(tmpPath)) return UpdaterSettings.Default;
                    json = File.ReadAllText(tmpPath);
                }
            }
            catch (IOException) { return UpdaterSettings.Default; }
            catch (UnauthorizedAccessException) { return UpdaterSettings.Default; }
            catch (ArgumentException) { return UpdaterSettings.Default; }
            catch (NotSupportedException) { return UpdaterSettings.Default; }

            return ReadFromJson(json);
        }

        // Internal seam for testing — writes to an arbitrary path with the
        // same atomic-rename semantics as WriteSettings.
        internal static bool WriteSettingsToPath(UpdaterSettings settings, string path)
        {
            string json;
            try
            {
                json = WriteToJson(settings);
            }
            catch (NotSupportedException) { return false; } // unsupported type — shouldn't happen on our shape
            catch (InvalidOperationException) { return false; }

            string? directory;
            try
            {
                directory = Path.GetDirectoryName(path);
            }
            catch (ArgumentException) { return false; }
            catch (PathTooLongException) { return false; }

            if (string.IsNullOrEmpty(directory)) return false;

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }

            var tempPath = path + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                // File.Move with overwrite is atomic on the same volume; we
                // are always on the same volume because both files live under
                // %LOCALAPPDATA%.
                File.Move(tempPath, path, overwrite: true);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally
            {
                // Defensive — if the rename failed mid-write, scrub the temp
                // so the next write starts clean.
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (IOException) { /* ignore */ }
                catch (UnauthorizedAccessException) { /* ignore */ }
            }
        }

        // Parses JSON into UpdaterSettings, returning Default on any failure.
        // Tolerant of missing fields — each property falls back to its
        // default independently, so adding new fields in future versions
        // does not invalidate existing on-disk files.
        internal static UpdaterSettings ReadFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return UpdaterSettings.Default;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException) { return UpdaterSettings.Default; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return UpdaterSettings.Default;

                var lastKspPath = TryReadString(root, "LastKspPath");
                var lastChannel = TryReadString(root, "LastChannelPreference");
                var retention = TryReadInt(root, "BackupRetention", UpdaterSettings.DefaultBackupRetention);

                // Negative retention is meaningless — treat as default.
                if (retention < 0) retention = UpdaterSettings.DefaultBackupRetention;

                return new UpdaterSettings(lastKspPath, lastChannel, retention);
            }
        }

        // Serialises UpdaterSettings to JSON. Indented for human inspection
        // — operators may want to hand-edit the file occasionally and a
        // wall-of-text JSON line is hostile.
        internal static string WriteToJson(UpdaterSettings settings)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                // No type discriminator, no PropertyNamingPolicy — keep the
                // shape stable and matching the field names directly.
            };

            // Build a JSON-friendly DTO inline so the on-disk shape is
            // independent of any future record-field renames.
            var dto = new SettingsDto
            {
                LastKspPath = settings.LastKspPath,
                LastChannelPreference = settings.LastChannelPreference,
                BackupRetention = settings.BackupRetention,
            };

            return JsonSerializer.Serialize(dto, options);
        }

        private static string? TryReadString(JsonElement parent, string property)
        {
            if (parent.TryGetProperty(property, out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            return null;
        }

        private static int TryReadInt(JsonElement parent, string property, int fallback)
        {
            if (parent.TryGetProperty(property, out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt32(out var value))
            {
                return value;
            }
            return fallback;
        }

        // DTO used purely for serialisation. Plain auto-properties so the
        // JSON shape is `{ "LastKspPath": "...", ... }` regardless of how
        // UpdaterSettings the record evolves.
        //
        // Forward-compat: adding a new property here is non-breaking — older
        // PlayerUpdater builds reading a settings.json written by a newer
        // build will silently ignore unknown fields (System.Text.Json
        // default behaviour). A downgrade then overwriting from the older
        // build drops the new field. That is acceptable for our preference-
        // bag use case; do not store anything load-bearing here.
        private sealed class SettingsDto
        {
            public string? LastKspPath { get; set; }
            public string? LastChannelPreference { get; set; }
            public int BackupRetention { get; set; }
        }
    }
}
