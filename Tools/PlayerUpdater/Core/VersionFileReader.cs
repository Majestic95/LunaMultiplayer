using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Reads the installed LMP version from a player's KSP install. The
    // primary signal is GameData/LunaMultiplayer/LunaMultiplayer.version
    // (a JSON file written by Scripts/build-release.ps1 at release time).
    // When that file is missing, malformed, or in the legacy pre-Piece-C
    // shape that lacks a TAG field, we fall back to reading the embedded
    // version metadata of GameData/LunaMultiplayer/Plugins/LmpClient.dll
    // via FileVersionInfo — the DLL's AssemblyInformationalVersion carries
    // the full release tag on builds emitted by build-release.ps1 (the
    // Set-LmpClientAssemblyInfoVersion helper there rewrites AssemblyInfo
    // before the Release build).
    //
    // Two file shapes are supported on the JSON side:
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
    //   revision=null + hotfix=null. This synthesis is the LAST-RESORT signal
    //   used only when the DLL fallback also fails — it's load-bearing only
    //   for the unusual install that has a stale .version but no DLLs (e.g.
    //   manual file drop, not a real LMP install).
    //
    // Resolution order in ReadInstalledVersion:
    //   1. JSON with TAG field         → authoritative, use it.
    //   2. DLL FileVersionInfo         → fallback. Rescues stale-.version
    //                                    installs (partial extract / skip-
    //                                    existing unzip / manual DLL drop).
    //   3. JSON legacy-shape synthesis → last-resort guess for installs with
    //                                    a stale .version and no DLL.
    //   4. null                        → nothing readable; caller surfaces
    //                                    "cohort unknown" UX.
    public static class VersionFileReader
    {
        // Path of the version file relative to the KSP root.
        public const string RelativeVersionPath = "GameData/LunaMultiplayer/LunaMultiplayer.version";

        // Path of the LmpClient DLL relative to the KSP root. Used by the
        // DLL-FileVersionInfo fallback when the JSON file is stale / missing.
        public const string RelativeLmpClientDllPath = "GameData/LunaMultiplayer/Plugins/LmpClient.dll";

        // Reads the installed LMP version from the given KSP install root.
        // Resolution order is JSON (authoritative when it has a TAG) → DLL
        // (rescues stale-.version installs) → JSON legacy-shape synthesis
        // (last-resort guess). Returns null only when ALL sources fail.
        public static VersionMetadata? ReadInstalledVersion(string kspInstallPath)
        {
            if (string.IsNullOrWhiteSpace(kspInstallPath)) return null;

            // Pass 1: try the JSON file. Track whether the result came from
            // an authoritative TAG field vs the legacy-shape synthesis so
            // we know whether to trust it or to prefer the DLL.
            var fromJson = TryReadJsonAtRoot(kspInstallPath, out var jsonHadTag);
            if (fromJson != null && jsonHadTag) return fromJson;

            // Pass 2: DLL fallback. The release-build pipeline embeds the
            // full release tag in LmpClient.dll's AssemblyInformationalVersion,
            // so a stale .version file (partial extract, "skip existing"
            // unzip, legacy upstream install) is rescued as soon as the DLLs
            // are current. We probe this AFTER JSON-with-TAG and BEFORE
            // legacy-shape synthesis so the DLL beats the synthesis when
            // both are present.
            var fromDll = ReadFromInstalledDll(kspInstallPath);
            if (fromDll != null) return fromDll;

            // Pass 3: fall back to whatever the JSON gave us (legacy-shape
            // synthesis, or null). Reaches this branch when neither an
            // authoritative TAG nor a DLL was readable.
            return fromJson;
        }

        // Reads the JSON file and returns the parsed metadata + a hint
        // indicating whether the result came from an authoritative TAG
        // field (true) or the legacy-shape synthesis (false). Used by
        // ReadInstalledVersion to decide whether to defer to the DLL
        // fallback. Returns null on any I/O or parse failure (hadTag=false).
        private static VersionMetadata? TryReadJsonAtRoot(string kspInstallPath, out bool hadTag)
        {
            hadTag = false;

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

            return ReadFromJson(json, out hadTag);
        }

        // Expected AssemblyProduct value for LMP-built DLLs. Identity check
        // against this string prevents the fallback from misidentifying a
        // non-LMP DLL that happens to live at the LmpClient path (e.g. a
        // stray rename, or someone copy-pasted an unrelated KSP mod's DLL
        // into the LMP folder while debugging). Without the check, a foreign
        // DLL with valid FileVersion bytes would be reported as a synthesised
        // stable LMP install — consumer-lens M1.
        //
        // Set in LmpClient/Properties/AssemblyInfo.cs as
        // `[assembly: AssemblyProduct("LMP")]` since the upstream-inherited
        // initial commit; the rename hazard is small but the cost of the
        // check is one string compare.
        private const string ExpectedLmpAssemblyProduct = "LMP";

        // Reads LmpClient.dll's embedded version metadata and parses it
        // into VersionMetadata. Returns null if the DLL is absent,
        // unreadable, not actually an LMP DLL (per AssemblyProduct check),
        // or carries no usable version data.
        //
        // Resolution within the DLL: AssemblyInformationalVersion is tried
        // first because release builds embed the FULL release tag there
        // (e.g. "v0.31.0-per-agency-private-11") via the release script's
        // Set-LmpClientAssemblyInfoVersion helper. If InformationalVersion
        // doesn't parse as a release tag (dev builds, pre-tag-embed release
        // DLLs that still carry the literal "0.31.0-compiled" string, hand-
        // built DLLs), fall back to AssemblyFileVersion's MAJOR.MINOR.PATCH
        // and synthesise a stable-channel tag — same shape as the JSON
        // legacy-shape synthesis. The synthesised result is best-effort:
        // it produces the right MAJOR.MINOR.PATCH but always claims the
        // stable channel, so a pre-tag-embed install on a private cohort
        // shows the cross-channel warning when re-installing the same
        // cohort's release. Documented limitation; resolves once every
        // cohort member has run an install of the tag-embedded build.
        public static VersionMetadata? ReadFromInstalledDll(string kspInstallPath)
        {
            if (string.IsNullOrWhiteSpace(kspInstallPath)) return null;

            var dllPath = Path.Combine(
                kspInstallPath,
                RelativeLmpClientDllPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(dllPath)) return null;

            FileVersionInfo info;
            try
            {
                info = FileVersionInfo.GetVersionInfo(dllPath);
            }
            catch (IOException)
            {
                // FileVersionInfo can throw FileNotFoundException (TOCTOU
                // race against the File.Exists check) or PathTooLongException
                // — both extend IOException.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                // Path contains invalid chars / null bytes.
                return null;
            }

            // Identity check: refuse to synthesise a version from a DLL that
            // isn't LMP. FileVersionInfo.ProductName is sourced from
            // [assembly: AssemblyProduct(...)] for managed assemblies.
            if (!string.Equals(info.ProductName, ExpectedLmpAssemblyProduct, StringComparison.Ordinal))
            {
                return null;
            }

            // FileVersionInfo.ProductVersion maps to [assembly: AssemblyInformationalVersion]
            // for managed assemblies — verified contract since .NET Core 3.0.
            return ParseFromFileVersionInfo(
                info.ProductVersion,
                info.FileMajorPart,
                info.FileMinorPart,
                info.FileBuildPart);
        }

        // Pure parser — testable without filesystem access. Takes the raw
        // FileVersionInfo fields and returns the best VersionMetadata we
        // can derive from them, or null if both InformationalVersion and
        // the synthetic FileVersion tag fail to parse.
        //
        // Visible for testing via `internal` — not part of the public API.
        internal static VersionMetadata? ParseFromFileVersionInfo(
            string? informationalVersion, int fileMajor, int fileMinor, int fileBuild)
        {
            // Pass 1: AssemblyInformationalVersion. Release builds emitted by
            // Scripts/build-release.ps1 carry the full release tag here.
            if (VersionParser.TryParse(informationalVersion, out var fromInfo) && fromInfo != null
                && fromInfo != VersionMetadata.Dev)
            {
                return fromInfo;
            }

            // Pass 2: AssemblyFileVersion (MAJOR.MINOR.PATCH). Reached for:
            //   - Dev builds (InformationalVersion is "0.31.0-compiled" which
            //     TryParse rejects)
            //   - Pre-Piece-C release DLLs (no tag embedded; assembly carries
            //     the literal "0.31.0-compiled" string)
            //   - InformationalVersion field is null or empty
            // Synthesise a stable-channel tag from the FileVersion parts. If
            // all parts are zero (DLL has no embedded version), produce
            // "v0.0.0" which parses as a stable v0.0.0 release — the
            // detection surface upstream will then offer to upgrade.
            if (fileMajor < 0 || fileMinor < 0 || fileBuild < 0) return null;

            var syntheticTag = string.Create(
                CultureInfo.InvariantCulture,
                $"v{fileMajor}.{fileMinor}.{fileBuild}");
            return VersionParser.TryParse(syntheticTag, out var fromFile) ? fromFile : null;
        }

        // Internal seam for testing — parses the JSON content directly without
        // touching the filesystem.
        internal static VersionMetadata? ReadFromJson(string json)
            => ReadFromJson(json, out _);

        // Internal overload exposing a `hadTag` hint. ReadInstalledVersion
        // uses this to decide whether the JSON result is authoritative (TAG
        // path → use it) or merely a legacy-shape synthesis (no TAG → prefer
        // the DLL fallback when available). The single-arg public/internal
        // ReadFromJson preserves the historical contract for existing tests
        // and callers that don't need the distinction.
        internal static VersionMetadata? ReadFromJson(string json, out bool hadTag)
        {
            hadTag = false;
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
                    if (VersionParser.TryParse(tag, out var meta) && meta != null)
                    {
                        hadTag = true;
                        return meta;
                    }
                    // TAG present but malformed — surface as null rather than
                    // falling back to VERSION synthesis. The legacy-shape
                    // synthesis would silently misclassify the install.
                    return null;
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

                // hadTag stays false — synthesised metadata is NOT authoritative.
                // ReadInstalledVersion uses this to defer to the DLL fallback.
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
