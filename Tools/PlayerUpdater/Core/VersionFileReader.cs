using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Reads GameData/LunaMultiplayer/LunaMultiplayer.version from a player's
    // KSP install and returns the parsed VersionMetadata. Two file shapes
    // are supported:
    //
    // Piece-C+ (templated by Scripts/build-release.ps1):
    //   { "TAG": "v0.31.0-per-agency-private-8.1", "CHANNEL": "per-agency-private",
    //     "REVISION": 8, "HOTFIX": 1, "VERSION": { "MAJOR": 0, "MINOR": 31, "PATCH": 0 }, ... }
    //   The TAG field is authoritative; CHANNEL + REVISION + HOTFIX + VERSION
    //   are build-time outputs of the parser and used as cross-checks during
    //   defensive reads — never re-derived here. HOTFIX is null when absent
    //   (e.g. 'v0.31.0-per-agency-private-7').
    //
    // Pre-Piece-C (legacy on-disk file, byte-for-byte from upstream 0.29.1):
    //   { "VERSION": { "MAJOR": 0, "MINOR": 29, "PATCH": 1 }, "GITHUB": {...}, ... }
    //   No TAG / CHANNEL / REVISION / HOTFIX fields. We synthesise a
    //   "vMAJOR.MINOR.PATCH" tag, default the channel to stable (these installs
    //   were never on a private cohort; upstream only ships stable), and return
    //   revision=null + hotfix=null.
    //
    // Any I/O or JSON failure returns null — callers should treat null as
    // "could not detect installed version" and let the player pick from a
    // list rather than refusing to proceed.
    public static class VersionFileReader
    {
        // Path of the version file relative to the KSP root.
        public const string RelativeVersionPath = "GameData/LunaMultiplayer/LunaMultiplayer.version";

        // Reads the version file from the given KSP install root. Returns
        // null if the file is missing, unreadable, or malformed.
        public static VersionMetadata? ReadInstalledVersion(string kspInstallPath)
        {
            if (string.IsNullOrWhiteSpace(kspInstallPath)) return null;

            // Consume RelativeVersionPath here (not Path.Combine of the raw
            // components) so an edit to the constant changes the actual read
            // too — otherwise the public-surface const drifts away from the
            // impl silently.
            var fullPath = Path.Combine(
                kspInstallPath,
                RelativeVersionPath.Replace('/', Path.DirectorySeparatorChar));

            string json;
            try
            {
                json = File.ReadAllText(fullPath);
            }
            catch (IOException)
            {
                // Covers PathTooLongException, DirectoryNotFoundException,
                // FileNotFoundException — all extend IOException in .NET 10.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                // Path contains invalid chars / null bytes. Reachable when
                // the future manual-picker fallback receives operator-pasted
                // text that isn't a clean filesystem path.
                return null;
            }
            catch (NotSupportedException)
            {
                // Path is in an unsupported format (e.g. embedded colon mid-string).
                return null;
            }

            return ReadFromJson(json);
        }

        // Internal seam for testing — parses the JSON content directly without
        // touching the filesystem.
        internal static VersionMetadata? ReadFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                if (root.TryGetProperty("TAG", out var tagProp) && tagProp.ValueKind == JsonValueKind.String)
                {
                    // Piece-C+ shape: TAG is authoritative.
                    var tag = tagProp.GetString();
                    return VersionParser.TryParse(tag, out var meta) ? meta : null;
                }

                // Pre-Piece-C fallback: synthesise from VERSION.MAJOR/MINOR/PATCH.
                if (!root.TryGetProperty("VERSION", out var versionProp)
                    || versionProp.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!TryReadInt(versionProp, "MAJOR", out var major)
                    || !TryReadInt(versionProp, "MINOR", out var minor)
                    || !TryReadInt(versionProp, "PATCH", out var patch))
                {
                    return null;
                }

                var syntheticTag = string.Create(
                    CultureInfo.InvariantCulture,
                    $"v{major}.{minor}.{patch}");

                return new VersionMetadata(
                    Tag: syntheticTag,
                    Major: major,
                    Minor: minor,
                    Patch: patch,
                    Channel: VersionMetadata.ChannelStable,
                    Revision: null,
                    Hotfix: null);
            }
        }

        private static bool TryReadInt(JsonElement parent, string property, out int value)
        {
            value = 0;
            return parent.TryGetProperty(property, out var prop)
                && prop.ValueKind == JsonValueKind.Number
                && prop.TryGetInt32(out value);
        }
    }
}
