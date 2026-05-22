using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LunaMultiplayer.PlayerUpdater.Core.Backups
{
    // JSON shape + read/write helpers for the per-backup manifest.json that
    // ships alongside the in-progress.marker inside every backup directory.
    // Extracted from BackupManager to keep that file under the 600-line soft
    // cap — same precedent as VdfTokenizer for KspDetector. The manifest is
    // a defined wire shape; isolating its serialiser here makes a future
    // schema bump (ManifestSchemaVersion += 1 + new field) a single-file
    // change.
    //
    // Schema v1:
    //   {
    //     "SchemaVersion": 1,
    //     "InstallPath":   "<canonical install path>",
    //     "TimestampUtc":  "<RFC 3339>",
    //     "ReplacingTag":  "<tag being installed over, or null>",
    //     "Entries":       [ "<zip entry path>", ... ]
    //   }
    //
    // Entries uses forward-slash separators (zip-native); InstallPath uses
    // the OS-native separators of the install side.
    internal static class BackupManifest
    {
        public const int SchemaVersion = 1;

        // Writes a v1 manifest to the given path. Plan items contribute one
        // entry each, in plan-iteration order so the manifest's Entries
        // array matches the on-disk file layout 1:1.
        public static void Write(
            string manifestPath,
            string canonicalInstall,
            IReadOnlyList<BackupAction> plan,
            string? replacingTag,
            DateTime timestampUtc)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"SchemaVersion\":").Append(SchemaVersion);
            sb.Append(",\"InstallPath\":");
            AppendJsonString(sb, canonicalInstall);
            sb.Append(",\"TimestampUtc\":");
            AppendJsonString(sb, timestampUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            sb.Append(",\"ReplacingTag\":");
            if (replacingTag is null)
            {
                sb.Append("null");
            }
            else
            {
                AppendJsonString(sb, replacingTag);
            }
            sb.Append(",\"Entries\":[");
            for (var i = 0; i < plan.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, plan[i].ZipEntryPath);
            }
            sb.Append("]}");

            File.WriteAllText(
                manifestPath,
                sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // Peeks at ReplacingTag + InstallPath without parsing the whole
        // entry list. ListBackups surfaces these for the Forms restore
        // dialog. A malformed or pre-schema manifest produces null values
        // and the rest of ListBackups still works.
        public static bool TryReadFields(
            string manifestPath,
            out string? replacingTag,
            out string? installPath)
        {
            replacingTag = null;
            installPath = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                if (root.TryGetProperty("ReplacingTag", out var tagProp)
                    && tagProp.ValueKind == JsonValueKind.String)
                {
                    replacingTag = tagProp.GetString();
                }
                if (root.TryGetProperty("InstallPath", out var pathProp)
                    && pathProp.ValueKind == JsonValueKind.String)
                {
                    installPath = pathProp.GetString();
                }
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                return false;
            }
        }

        // Minimal manual JSON string escape so we never pull
        // System.Text.Json.JsonWriter just for a 5-field record.
        // Mirrors the System.Text.Json escape rules for the characters that
        // can appear in install paths and zip entry paths (backslashes on
        // Windows, slashes, quotes, control chars).
        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
