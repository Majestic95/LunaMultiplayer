using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LunaMultiplayer.PlayerUpdater.Core.Vdf;
using Microsoft.Win32;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Locates an existing KSP1 install. The chain is Steam → CKAN → GOG →
    // operator-supplied last-used path → operator-supplied manual pick. The
    // Forms layer drives the picker; this class is the pure-detection surface
    // and never opens UI.
    //
    // Every candidate path goes through ValidateKspPath before being returned
    // to the caller — a path that does not have GameData/Squad/ underneath it
    // is rejected. This guards against:
    //   - a stale Steam library entry where the player moved KSP elsewhere
    //   - a partially-unmounted external drive where the library path still
    //     resolves but the install contents are gone
    //   - operator-pasted text in the manual-picker fallback that points at
    //     the wrong directory
    //
    // Live-tested surfaces (this development machine, 2026-05-20):
    //   - Steam libraryfolders.vdf v2 (path + apps blocks) — operator's actual file
    //   - ValidateKspPath against operator's F:/SteamLibrary KSP install
    //
    // NOT live-tested (operator does not use these tools):
    //   - CKAN registry / instance file — defensive probe against documented format.
    //     Returns null on any mismatch; tested against synthetic fixtures only.
    //   - GOG registry layout — defensive probe of HKLM\SOFTWARE\GOG.com. Returns
    //     null on any mismatch; cannot fixture-test the registry.
    // If a player reports a CKAN or GOG detection failure, exercise the relevant
    // probe against their actual on-disk file and extend the parser then. The
    // shape is intentionally tolerant so a wrong guess about CKAN's format fails
    // silent-and-null rather than throwing.
    public static class KspDetector
    {
        // Steam appid for Kerbal Space Program (the KSP1 release).
        public const int KspSteamAppId = 220200;

        // GOG productId for Kerbal Space Program Enhanced Edition.
        // Best-effort — GOG's per-product registry layout is the documented
        // shape used by other community tools (e.g. CKAN's GOG detector).
        public const int KspGogProductId = 1429864849;

        // Subdirectory whose presence confirms a candidate path is a real KSP
        // install. Squad is the stock-content gameplay folder and is present
        // on every KSP1 install regardless of mod loadout.
        public const string KspContentSentinelRelative = "GameData/Squad";

        // Parsed Steam library entry — one entry per { } block under the
        // top-level "libraryfolders" object.
        public sealed record SteamLibrary(string Path, IReadOnlyList<int> AppIds);

        // Parses the contents of libraryfolders.vdf into the library entries.
        // Returns an empty list if the input is malformed in any way — this
        // method must not throw, the Forms layer treats Steam detection as a
        // best-effort source not a load-bearing one.
        public static IReadOnlyList<SteamLibrary> ParseSteamLibraryFolders(string vdfContent)
        {
            if (string.IsNullOrWhiteSpace(vdfContent)) return Array.Empty<SteamLibrary>();

            List<VdfToken> tokens;
            try
            {
                tokens = VdfTokenizer.Tokenize(vdfContent);
            }
            catch (FormatException)
            {
                return Array.Empty<SteamLibrary>();
            }

            var libraries = new List<SteamLibrary>();
            var cursor = 0;

            // Top-level wrapper key — "libraryfolders" — followed by '{' ... '}'.
            // Tolerate the input being just the inner block too (some Steam
            // versions historically wrote the file without the wrapper).
            if (TryConsumeNamedBlock(tokens, ref cursor, "libraryfolders", out var innerStart, out var innerEnd))
            {
                ExtractLibrariesFromBlock(tokens, innerStart, innerEnd, libraries);
            }
            else
            {
                // No wrapper — treat the whole token stream as the contents.
                ExtractLibrariesFromBlock(tokens, 0, tokens.Count, libraries);
            }

            return libraries;
        }

        // Returns the candidate KSP path under the given Steam library, or
        // null if KSP is not actually installed under that library.
        public static string? ResolveKspUnderSteamLibrary(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath)) return null;
            var candidate = Path.Combine(libraryPath, "steamapps", "common", "Kerbal Space Program");
            return ValidateKspPath(candidate) ? candidate : null;
        }

        // Reads the Steam install path from the registry. Falls back to the
        // standard 32-bit Program Files location if the registry key is
        // missing (rare — Steam writes it on every install).
        public static string? FindSteamRootPath()
        {
#pragma warning disable CA1416 // AssemblyInfo declares windows6.1 platform support — registry access is fine
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                    ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                var installPath = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
                {
                    return installPath;
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
#pragma warning restore CA1416

            // Fallback: the default Steam install location.
            var fallback = @"C:\Program Files (x86)\Steam";
            return Directory.Exists(fallback) ? fallback : null;
        }

        // Reads libraryfolders.vdf from a Steam root and returns every KSP
        // install resolvable under those libraries. Probes both the modern
        // (steamapps/) and legacy (config/) locations — newer Steam writes
        // the file to steamapps/, older Steam to config/. Both copies are
        // typically identical when both exist.
        public static IEnumerable<string> EnumerateSteamKspPaths(string? steamRootPath)
        {
            if (string.IsNullOrWhiteSpace(steamRootPath)) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var probePaths = new[]
            {
                Path.Combine(steamRootPath, "steamapps", "libraryfolders.vdf"),
                Path.Combine(steamRootPath, "config", "libraryfolders.vdf"),
            };

            foreach (var vdfPath in probePaths)
            {
                string? content;
                try
                {
                    if (!File.Exists(vdfPath)) continue;
                    content = File.ReadAllText(vdfPath);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var library in ParseSteamLibraryFolders(content))
                {
                    // Probe every library — do NOT pre-filter on the apps
                    // block listing KspSteamAppId. A library whose apps cache
                    // is stale (e.g. operator moved KSP manually post-install)
                    // would otherwise be silently skipped even though the
                    // install is on disk. ValidateKspPath gates the final
                    // resolution, and the per-library Directory.Exists cost
                    // is microseconds on a warm drive (single-digit ms on a
                    // sleeping HDD waking — once per detection).
                    var resolved = ResolveKspUnderSteamLibrary(library.Path);
                    if (resolved != null && seen.Add(resolved))
                    {
                        yield return resolved;
                    }
                }
            }
        }

        // Defensive probe of CKAN's per-instance config. CKAN stores registered
        // KSP instances at %LOCALAPPDATA%/CKAN/instances.json (post-1.30) or
        // %APPDATA%/CKAN/registry.json (older). We try the documented modern
        // path first; on any I/O or JSON failure we return null without
        // surfacing the error.
        //
        // NOT live-tested — return null on this dev machine because the operator
        // does not use CKAN. Format assumptions are from the public ksp-ckan/CKAN
        // repository; treat extension as a follow-up if a player reports a miss.
        public static IEnumerable<string> EnumerateCkanKspPaths()
        {
            // Format documented in the upstream CKAN repository: instances.json
            // contains an object with an "instances" key mapping instance name
            // to a sub-object containing a "path" string field.
            //
            // We do NOT call System.Text.Json directly here — the dependency
            // surface lives in VersionFileReader and we want this probe to be
            // self-contained. Instead, we do a minimal substring scan for the
            // documented field name and parse the adjacent quoted string. The
            // scan is tolerant of trailing whitespace and forgiving on the
            // surrounding shape; on any mismatch we yield nothing.
            string? content;
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData)) yield break;

                var instancesPath = Path.Combine(localAppData, "CKAN", "instances.json");
                if (!File.Exists(instancesPath)) yield break;

                content = File.ReadAllText(instancesPath);
            }
            catch (IOException) { yield break; }
            catch (UnauthorizedAccessException) { yield break; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in ExtractJsonPathFields(content))
            {
                if (ValidateKspPath(candidate) && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        // Defensive probe of GOG's registry layout for the KSP Enhanced Edition
        // product. NOT live-tested — return null on this dev machine because
        // the operator does not have a GOG install.
        public static string? FindGogKspPath()
        {
#pragma warning disable CA1416 // AssemblyInfo declares windows6.1 platform support
            try
            {
                var subKeyPath = string.Create(
                    CultureInfo.InvariantCulture,
                    $@"SOFTWARE\WOW6432Node\GOG.com\Games\{KspGogProductId}");

                using var key = Registry.LocalMachine.OpenSubKey(subKeyPath)
                    ?? Registry.LocalMachine.OpenSubKey(
                        string.Create(CultureInfo.InvariantCulture, $@"SOFTWARE\GOG.com\Games\{KspGogProductId}"));

                if (key?.GetValue("path") is string installPath
                    && ValidateKspPath(installPath))
                {
                    return installPath;
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
#pragma warning restore CA1416

            return null;
        }

        // Returns true if the given path looks like a real KSP install.
        // Required: directory exists AND GameData/Squad subdirectory exists.
        // We do NOT require GameData/LunaMultiplayer to exist — a first-time
        // installer running against a vanilla KSP must succeed.
        //
        // Operator hint for the Forms layer (sub-slice 4): when this returns
        // false, distinguish "directory genuinely absent" from "I/O failure
        // probing the directory" via DriveInfo before showing the operator
        // a path-not-found dialog. A mapped network drive that's offline
        // (e.g. Z:\KSP where the NAS is powered down) returns false here
        // identically to "wrong path" — surfacing that distinction in the UI
        // saves an operator-confused support ticket. Relative paths are
        // normalised via Path.GetFullPath before probing so an operator
        // pasting "..\KSP" doesn't accidentally validate against the
        // updater's CWD.
        public static bool ValidateKspPath(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            string normalised;
            try
            {
                normalised = Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            // PathTooLongException is an IOException subtype on net10.0,
            // covered by the IOException catch.
            catch (IOException) { return false; }

            try
            {
                if (!Directory.Exists(normalised)) return false;

                var sentinel = Path.Combine(normalised, KspContentSentinelRelative.Replace('/', Path.DirectorySeparatorChar));
                return Directory.Exists(sentinel);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        // Enumerates every detection-chain candidate in priority order. The
        // caller deduplicates if it cares — the chain may yield the same path
        // from multiple sources (e.g. CKAN registered the operator's Steam
        // install). Callers iterate until the first ValidateKspPath-passing
        // result, or fall through to the manual picker if nothing yields.
        //
        // lastUsedPath comes from SettingsStore and represents the operator's
        // previous choice. It is yielded FIRST so a player who installed once
        // is immediately re-detected; the rest of the chain handles fresh
        // installs and post-move scenarios.
        public static IEnumerable<string> EnumerateCandidates(string? lastUsedPath = null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(lastUsedPath)
                && ValidateKspPath(lastUsedPath)
                && seen.Add(lastUsedPath))
            {
                yield return lastUsedPath;
            }

            var steamRoot = FindSteamRootPath();
            foreach (var path in EnumerateSteamKspPaths(steamRoot))
            {
                if (seen.Add(path)) yield return path;
            }

            foreach (var path in EnumerateCkanKspPaths())
            {
                if (seen.Add(path)) yield return path;
            }

            var gogPath = FindGogKspPath();
            if (gogPath != null && seen.Add(gogPath)) yield return gogPath;
        }

        // --- internal helpers ---

        // Walks the token stream extracting libraries from the current block.
        // start..end is a half-open token range over [{...}] body tokens
        // (excluding the enclosing braces).
        private static void ExtractLibrariesFromBlock(
            IReadOnlyList<VdfToken> tokens, int start, int end, List<SteamLibrary> output)
        {
            var i = start;
            while (i < end)
            {
                // Each library is keyed by an integer-as-string ("0", "1", ...)
                // followed by an open brace. Anything else here we skip past
                // (defensive against future Steam writing scalar values at
                // this level — we just won't index them).
                if (tokens[i].Kind != VdfTokenKind.String)
                {
                    i++;
                    continue;
                }

                if (i + 1 >= end || tokens[i + 1].Kind != VdfTokenKind.OpenBrace)
                {
                    // Defensive: scalar at top level (rare) — skip the pair.
                    // But if the next token is a CloseBrace, advance by one
                    // and let the outer loop's `i < end` termination handle
                    // it; we must NOT walk past a structurally-meaningful
                    // close.
                    if (i + 1 < end && tokens[i + 1].Kind == VdfTokenKind.CloseBrace)
                    {
                        i++;
                    }
                    else
                    {
                        i += 2;
                    }
                    continue;
                }

                if (!TryFindMatchingBrace(tokens, i + 1, end, out var bodyEnd))
                {
                    // Malformed — bail rather than throw.
                    return;
                }

                ExtractSingleLibrary(tokens, i + 2, bodyEnd, output);
                i = bodyEnd + 1; // skip past the closing brace
            }
        }

        // bodyStart..bodyEnd is half-open over the tokens INSIDE one library's
        // { ... } block. Reads the path and apps entries.
        private static void ExtractSingleLibrary(
            IReadOnlyList<VdfToken> tokens, int bodyStart, int bodyEnd, List<SteamLibrary> output)
        {
            string? path = null;
            List<int>? appIds = null;

            var i = bodyStart;
            while (i < bodyEnd)
            {
                if (tokens[i].Kind != VdfTokenKind.String) { i++; continue; }
                var key = tokens[i].Value;

                if (i + 1 >= bodyEnd) break;

                if (tokens[i + 1].Kind == VdfTokenKind.String)
                {
                    // Scalar key/value pair.
                    if (string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
                    {
                        path = tokens[i + 1].Value;
                    }
                    i += 2;
                }
                else if (tokens[i + 1].Kind == VdfTokenKind.OpenBrace)
                {
                    if (!TryFindMatchingBrace(tokens, i + 1, bodyEnd, out var nestedEnd))
                    {
                        return;
                    }

                    if (string.Equals(key, "apps", StringComparison.OrdinalIgnoreCase))
                    {
                        appIds = ExtractAppIds(tokens, i + 2, nestedEnd);
                    }

                    i = nestedEnd + 1;
                }
                else
                {
                    // Unexpected token — skip it.
                    i++;
                }
            }

            if (path != null)
            {
                output.Add(new SteamLibrary(path, appIds ?? (IReadOnlyList<int>)Array.Empty<int>()));
            }
        }

        private static List<int> ExtractAppIds(IReadOnlyList<VdfToken> tokens, int start, int end)
        {
            var ids = new List<int>();
            for (var i = start; i + 1 < end; i += 2)
            {
                if (tokens[i].Kind == VdfTokenKind.String
                    && tokens[i + 1].Kind == VdfTokenKind.String
                    && int.TryParse(tokens[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
                {
                    ids.Add(appId);
                }
            }
            return ids;
        }

        private static bool TryConsumeNamedBlock(
            IReadOnlyList<VdfToken> tokens, ref int cursor, string name, out int bodyStart, out int bodyEnd)
        {
            bodyStart = 0;
            bodyEnd = 0;

            if (cursor + 1 >= tokens.Count) return false;
            if (tokens[cursor].Kind != VdfTokenKind.String) return false;
            if (!string.Equals(tokens[cursor].Value, name, StringComparison.OrdinalIgnoreCase)) return false;
            if (tokens[cursor + 1].Kind != VdfTokenKind.OpenBrace) return false;

            if (!TryFindMatchingBrace(tokens, cursor + 1, tokens.Count, out var close)) return false;

            bodyStart = cursor + 2;
            bodyEnd = close;
            cursor = close + 1;
            return true;
        }

        private static bool TryFindMatchingBrace(
            IReadOnlyList<VdfToken> tokens, int openIndex, int searchEnd, out int closeIndex)
        {
            closeIndex = 0;
            // Defensive bounds — every caller already guards openIndex against
            // the relevant tokens.Count / searchEnd cutoff, but the alternative
            // (IndexOutOfRangeException) is uglier than a clean false return.
            if (openIndex < 0 || openIndex >= tokens.Count) return false;
            if (tokens[openIndex].Kind != VdfTokenKind.OpenBrace) return false;

            var depth = 1;
            for (var j = openIndex + 1; j < searchEnd; j++)
            {
                if (tokens[j].Kind == VdfTokenKind.OpenBrace) depth++;
                else if (tokens[j].Kind == VdfTokenKind.CloseBrace)
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeIndex = j;
                        return true;
                    }
                }
            }
            return false;
        }

        // Scans JSON-ish content for every `"path": "..."` field and yields
        // the unescaped string value. Used by the CKAN probe — we avoid
        // pulling System.Text.Json here to keep the probe self-contained and
        // tolerant of CKAN's actual file shape (which may carry comments or
        // non-strict JSON forms that JsonDocument would reject outright).
        private static IEnumerable<string> ExtractJsonPathFields(string content)
        {
            if (string.IsNullOrEmpty(content)) yield break;

            // Match either GameDir (CKAN's documented instance-config field
            // for the KSP install root) or path (more generic) — we try both
            // to widen tolerance against format drift.
            //
            // The field-name match is closing-quote-anchored ("GameDir") so
            // a longer key like "GameDir_alias" cannot match by prefix; the
            // closing quote must appear before any other character.
            foreach (var fieldName in new[] { "GameDir", "path" })
            {
                var pattern = $"\"{fieldName}\"";
                var searchStart = 0;
                while (true)
                {
                    var fieldIdx = content.IndexOf(pattern, searchStart, StringComparison.Ordinal);
                    if (fieldIdx < 0) break;

                    var colonIdx = content.IndexOf(':', fieldIdx + pattern.Length);
                    if (colonIdx < 0) break;

                    // Ensure nothing-but-whitespace between the closing quote
                    // and the colon. Defends against shapes like
                    //   "GameDir" "alias": "..."
                    // where the closing quote of "GameDir" matches but the
                    // colon belongs to a different key.
                    var allWhitespace = true;
                    for (var w = fieldIdx + pattern.Length; w < colonIdx; w++)
                    {
                        if (!char.IsWhiteSpace(content[w])) { allWhitespace = false; break; }
                    }
                    if (!allWhitespace)
                    {
                        searchStart = fieldIdx + pattern.Length;
                        continue;
                    }

                    var valueStart = colonIdx + 1;
                    // Skip whitespace.
                    while (valueStart < content.Length && char.IsWhiteSpace(content[valueStart])) valueStart++;

                    if (valueStart >= content.Length || content[valueStart] != '"')
                    {
                        searchStart = fieldIdx + pattern.Length;
                        continue;
                    }

                    var sb = new System.Text.StringBuilder();
                    var k = valueStart + 1;
                    var closed = false;
                    while (k < content.Length)
                    {
                        var ch = content[k];
                        if (ch == '\\' && k + 1 < content.Length)
                        {
                            var next = content[k + 1];
                            sb.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : next == 't' ? '\t' : next);
                            k += 2;
                            continue;
                        }
                        if (ch == '"') { closed = true; break; }
                        sb.Append(ch);
                        k++;
                    }

                    searchStart = k + 1;
                    if (closed && sb.Length > 0)
                    {
                        yield return sb.ToString();
                    }
                }
            }
        }

    }
}
