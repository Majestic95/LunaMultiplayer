using System.Diagnostics;
using System.IO;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class VersionFileReaderTest
    {
        // --- Piece-C+ shape (TAG is authoritative) ---

        [TestMethod]
        public void ReadFromJson_PieceCShape_UsesTagField()
        {
            var json = """
            {
                "NAME":     "Luna Multiplayer (Majestic95 fork)",
                "VERSION":  { "MAJOR": 0, "MINOR": 31, "PATCH": 0 },
                "TAG":      "v0.31.0-per-agency-private-7",
                "CHANNEL":  "per-agency-private",
                "REVISION": 7
            }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.31.0-per-agency-private-7", meta!.Tag);
            Assert.AreEqual(VersionMetadata.ChannelPerAgencyPrivate, meta.Channel);
            Assert.AreEqual(7, meta.Revision);
            Assert.AreEqual(31, meta.Minor);
        }

        [TestMethod]
        public void ReadFromJson_PieceCShapeWithStableTag_ParsesAsStable()
        {
            var json = """
            { "TAG": "v1.0.0", "VERSION": { "MAJOR": 1, "MINOR": 0, "PATCH": 0 } }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNotNull(meta);
            Assert.AreEqual(VersionMetadata.ChannelStable, meta!.Channel);
            Assert.IsNull(meta.Revision);
        }

        [TestMethod]
        public void ReadFromJson_PieceCShapeWithDevTag_ReturnsDevSentinel()
        {
            var json = """
            { "TAG": "v0.0.0-dev" }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.AreSame(VersionMetadata.Dev, meta);
        }

        [TestMethod]
        public void ReadFromJson_PieceCShapeWithInvalidTag_ReturnsNull()
        {
            // TAG is present but malformed — VersionParser.TryParse returns false,
            // VersionFileReader propagates that as null rather than falling back
            // to VERSION (which would silently misclassify the install).
            var json = """
            { "TAG": "v0.31.0-private", "VERSION": { "MAJOR": 0, "MINOR": 31, "PATCH": 0 } }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNull(meta);
        }

        // --- Pre-Piece-C legacy shape (TAG / CHANNEL / REVISION absent) ---

        [TestMethod]
        public void ReadFromJson_LegacyShape_SynthesizesStableTagWithNullRevision()
        {
            // The on-disk LunaMultiplayer.version shipped by upstream 0.29.1
            // before any Majestic95 release was bundled.
            var json = """
            {
                "NAME":     "Luna Multiplayer",
                "URL":      "https://github.com/LunaMultiplayer/LunaMultiplayer/raw/master/LunaMultiplayer.version",
                "DOWNLOAD": "https://github.com/LunaMultiplayer/LunaMultiplayer/releases",
                "GITHUB": {
                    "USERNAME":   "LunaMultiplayer",
                    "REPOSITORY": "LunaMultiplayer"
                },
                "VERSION": { "MAJOR": 0, "MINOR": 29, "PATCH": 1 },
                "KSP_VERSION": { "MAJOR": 1, "MINOR": 12 }
            }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.29.1", meta!.Tag);
            Assert.AreEqual(0, meta.Major);
            Assert.AreEqual(29, meta.Minor);
            Assert.AreEqual(1, meta.Patch);
            Assert.AreEqual(VersionMetadata.ChannelStable, meta.Channel);
            Assert.IsNull(meta.Revision);
        }

        [TestMethod]
        public void ReadFromJson_LegacyShapeMissingVersionBlock_ReturnsNull()
        {
            var json = """
            { "NAME": "Luna Multiplayer", "GITHUB": {} }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNull(meta);
        }

        [TestMethod]
        public void ReadFromJson_LegacyShapeMissingPatchField_ReturnsNull()
        {
            var json = """
            { "VERSION": { "MAJOR": 0, "MINOR": 29 } }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNull(meta);
        }

        [TestMethod]
        public void ReadFromJson_LegacyShapeWithStringPatch_ReturnsNull()
        {
            // Defensive: MAJOR/MINOR/PATCH must be numbers, not strings.
            var json = """
            { "VERSION": { "MAJOR": 0, "MINOR": 29, "PATCH": "1" } }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNull(meta);
        }

        // --- Robustness: malformed / empty / wrong-root inputs ---

        [TestMethod]
        public void ReadFromJson_EmptyString_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadFromJson(""));
        }

        [TestMethod]
        public void ReadFromJson_Whitespace_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadFromJson("   "));
        }

        [TestMethod]
        public void ReadFromJson_MalformedJson_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadFromJson("{ \"TAG\": "));
        }

        [TestMethod]
        public void ReadFromJson_NonObjectRoot_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadFromJson("[1, 2, 3]"));
        }

        [TestMethod]
        public void ReadFromJson_EmptyObject_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadFromJson("{}"));
        }

        [TestMethod]
        public void ReadFromJson_TagAsNonString_FallsThroughToVersionBlock()
        {
            // Defensive: if TAG is present but the wrong JSON type (numeric here),
            // we ignore it and try the legacy VERSION-block fallback. Otherwise a
            // malformed file shape with a numeric TAG would silently misread.
            var json = """
            { "TAG": 42, "VERSION": { "MAJOR": 0, "MINOR": 29, "PATCH": 1 } }
            """;

            var meta = VersionFileReader.ReadFromJson(json);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.29.1", meta!.Tag);
            Assert.AreEqual(VersionMetadata.ChannelStable, meta.Channel);
        }

        // --- Filesystem-touching public API ---

        [TestMethod]
        public void ReadInstalledVersion_NullPath_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadInstalledVersion(null!));
        }

        [TestMethod]
        public void ReadInstalledVersion_EmptyPath_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadInstalledVersion(""));
        }

        [TestMethod]
        public void ReadInstalledVersion_NonExistentPath_ReturnsNull()
        {
            var phantom = Path.Combine(Path.GetTempPath(), "lmp-test-nonexistent-" + Path.GetRandomFileName());

            Assert.IsNull(VersionFileReader.ReadInstalledVersion(phantom));
        }

        [TestMethod]
        public void ReadInstalledVersion_RealFile_ReadsPieceCShape()
        {
            // Plant a Piece-C-shape file under a temp KSP root and verify the
            // public API constructs the expected GameData/LunaMultiplayer/...
            // path and reads it correctly.
            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var versionDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer");
                Directory.CreateDirectory(versionDir);
                File.WriteAllText(
                    Path.Combine(versionDir, "LunaMultiplayer.version"),
                    "{ \"TAG\": \"v0.31.0\", \"VERSION\": { \"MAJOR\": 0, \"MINOR\": 31, \"PATCH\": 0 } }");

                var meta = VersionFileReader.ReadInstalledVersion(kspRoot);

                Assert.IsNotNull(meta);
                Assert.AreEqual("v0.31.0", meta!.Tag);
                Assert.AreEqual(VersionMetadata.ChannelStable, meta.Channel);
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        // --- hadTag overload (used by ReadInstalledVersion to decide DLL deferral) ---

        [TestMethod]
        public void ReadFromJson_OverloadWithTag_ReportsHadTagTrue()
        {
            var json = "{ \"TAG\": \"v0.31.0-per-agency-private-7\", " +
                       "\"VERSION\": { \"MAJOR\": 0, \"MINOR\": 31, \"PATCH\": 0 } }";

            var meta = VersionFileReader.ReadFromJson(json, out var hadTag);

            Assert.IsNotNull(meta);
            Assert.IsTrue(hadTag, "Authoritative TAG path must set hadTag=true so the orchestrator skips the DLL fallback.");
        }

        [TestMethod]
        public void ReadFromJson_OverloadLegacyShape_ReportsHadTagFalse()
        {
            var json = "{ \"VERSION\": { \"MAJOR\": 0, \"MINOR\": 29, \"PATCH\": 1 } }";

            var meta = VersionFileReader.ReadFromJson(json, out var hadTag);

            Assert.IsNotNull(meta);
            Assert.IsFalse(hadTag, "Legacy-shape synthesis must NOT be flagged as authoritative — the orchestrator should prefer the DLL fallback over this.");
        }

        [TestMethod]
        public void ReadFromJson_OverloadMissingFile_ReportsHadTagFalse()
        {
            var meta = VersionFileReader.ReadFromJson("", out var hadTag);

            Assert.IsNull(meta);
            Assert.IsFalse(hadTag);
        }

        [TestMethod]
        public void ReadFromJson_OverloadMalformedTag_ReportsHadTagFalse()
        {
            // TAG present but unparseable. The reader returns null AND
            // does NOT mark the result as authoritative — but since meta
            // is null the orchestrator falls through to the DLL anyway.
            var json = "{ \"TAG\": \"v0.31.0-private\", \"VERSION\": { \"MAJOR\": 0, \"MINOR\": 31, \"PATCH\": 0 } }";

            var meta = VersionFileReader.ReadFromJson(json, out var hadTag);

            Assert.IsNull(meta);
            Assert.IsFalse(hadTag);
        }

        // --- ParseFromFileVersionInfo (pure helper, no filesystem) ---

        [TestMethod]
        public void ParseFromFileVersionInfo_InformationalVersionIsReleaseTag_UsesTag()
        {
            // Release builds emit the full tag in AssemblyInformationalVersion.
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                "v0.31.0-per-agency-private-11",
                fileMajor: 0, fileMinor: 31, fileBuild: 0);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.31.0-per-agency-private-11", meta!.Tag);
            Assert.AreEqual(VersionMetadata.ChannelPerAgencyPrivate, meta.Channel);
            Assert.AreEqual(11, meta.Revision);
            Assert.IsNull(meta.Hotfix);
        }

        [TestMethod]
        public void ParseFromFileVersionInfo_InformationalVersionIsReleaseTagWithHotfix_UsesTag()
        {
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                "v0.31.0-per-agency-private-8.1",
                fileMajor: 0, fileMinor: 31, fileBuild: 0);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.31.0-per-agency-private-8.1", meta!.Tag);
            Assert.AreEqual(8, meta.Revision);
            Assert.AreEqual(1, meta.Hotfix);
        }

        [TestMethod]
        public void ParseFromFileVersionInfo_InformationalVersionIsCompiledSuffix_FallsBackToFileVersion()
        {
            // Pre-Piece-C and dev builds embed "0.30.0-compiled" /
            // "0.31.0-compiled" which VersionParser rejects — fall back
            // to the FileVersion parts and synthesise a stable tag.
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                "0.31.0-compiled",
                fileMajor: 0, fileMinor: 31, fileBuild: 0);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.31.0", meta!.Tag);
            Assert.AreEqual(VersionMetadata.ChannelStable, meta.Channel);
            Assert.AreEqual(31, meta.Minor);
        }

        [TestMethod]
        public void ParseFromFileVersionInfo_InformationalVersionIsNull_FallsBackToFileVersion()
        {
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                informationalVersion: null,
                fileMajor: 0, fileMinor: 31, fileBuild: 0);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.31.0", meta!.Tag);
        }

        [TestMethod]
        public void ParseFromFileVersionInfo_InformationalVersionIsEmpty_FallsBackToFileVersion()
        {
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                informationalVersion: "",
                fileMajor: 1, fileMinor: 2, fileBuild: 3);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v1.2.3", meta!.Tag);
        }

        [TestMethod]
        public void ParseFromFileVersionInfo_InformationalVersionIsDevSentinel_FallsBackToFileVersion()
        {
            // "v0.0.0-dev" parses as VersionMetadata.Dev. We treat it as
            // unhelpful (it's the local-dev sentinel, not a release) and
            // prefer the FileVersion parts so an installed DLL with real
            // numbers still gets a meaningful answer.
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                informationalVersion: VersionMetadata.DevTag,
                fileMajor: 0, fileMinor: 31, fileBuild: 0);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.31.0", meta!.Tag);
        }

        [TestMethod]
        public void ParseFromFileVersionInfo_NegativeFileVersionPart_ReturnsNull()
        {
            // FileMajorPart returns -1 when no version resource is embedded;
            // synthesising "v-1.0.0" would be a malformed tag, so we surface
            // null instead.
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                informationalVersion: null,
                fileMajor: -1, fileMinor: 0, fileBuild: 0);

            Assert.IsNull(meta);
        }

        [TestMethod]
        public void ParseFromFileVersionInfo_AllZeroFileVersion_SynthesizesZeroTag()
        {
            // Edge case: DLL has a real but all-zero FileVersion resource.
            // Pure-helper contract: it does math on whatever inputs the
            // wrapper hands it, including {0,0,0}. The "is this actually
            // LMP?" identity check lives in the WRAPPER (ReadFromInstalledDll)
            // via the AssemblyProduct=="LMP" check — see the dedicated
            // ReadFromInstalledDll_NonLmpProductName_ReturnsNull test below.
            // This test pins the pure-helper math; the wrapper test pins the
            // false-positive guard.
            var meta = VersionFileReader.ParseFromFileVersionInfo(
                informationalVersion: null,
                fileMajor: 0, fileMinor: 0, fileBuild: 0);

            Assert.IsNotNull(meta);
            Assert.AreEqual("v0.0.0", meta!.Tag);
            Assert.AreEqual(VersionMetadata.ChannelStable, meta.Channel);
        }

        // --- ReadFromInstalledDll (filesystem) ---

        [TestMethod]
        public void ReadFromInstalledDll_NullPath_ReturnsNull()
        {
            Assert.IsNull(VersionFileReader.ReadFromInstalledDll(null!));
        }

        [TestMethod]
        public void ReadFromInstalledDll_NoDll_ReturnsNull()
        {
            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(kspRoot);
                // No GameData/LunaMultiplayer/Plugins/LmpClient.dll planted.
                Assert.IsNull(VersionFileReader.ReadFromInstalledDll(kspRoot));
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadFromInstalledDll_NonLmpProductName_ReturnsNull()
        {
            // Defensive: a non-LMP DLL planted at the LmpClient path (rename
            // accident, copy-pasted mod DLL while debugging, etc.) must NOT
            // be detected as a valid LMP install. Without the ProductName
            // identity check, FileVersion synthesis would produce a synthetic
            // stable tag and the cohort-mismatch warning would silently skip.
            //
            // Use the MSTest framework DLL as the negative fixture — it ships
            // alongside the test runner so it's always available, and its
            // ProductName ("MSTest.Core") is reliably different from "LMP".
            var nonLmpDll = typeof(TestClassAttribute).Assembly.Location;

            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var pluginsDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer", "Plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(nonLmpDll, Path.Combine(pluginsDir, "LmpClient.dll"));

                var meta = VersionFileReader.ReadFromInstalledDll(kspRoot);

                Assert.IsNull(meta, "DLL with ProductName != 'LMP' must be rejected so an unrelated DLL at the LmpClient path isn't misdetected as a v0.X.Y stable LMP install.");
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadInstalledVersion_NonLmpDllAndNoJson_ReturnsNull()
        {
            // Orchestration counterpart: non-LMP DLL + no JSON → null
            // (not a synthesised v0.X.Y stable result). Closes the
            // consumer-lens M1 hazard end-to-end.
            var nonLmpDll = typeof(TestClassAttribute).Assembly.Location;

            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var pluginsDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer", "Plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(nonLmpDll, Path.Combine(pluginsDir, "LmpClient.dll"));

                var meta = VersionFileReader.ReadInstalledVersion(kspRoot);

                Assert.IsNull(meta);
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadFromInstalledDll_RealDll_ReadsEmbeddedVersion()
        {
            // Use the test assembly itself as the stand-in for LmpClient.dll —
            // every compiled .NET assembly has a FileVersion resource (the
            // SDK auto-generates one even when AssemblyFileVersion isn't set),
            // so this exercises the real FileVersionInfo path without needing
            // a checked-in fixture DLL. We assert against whatever FileVersion
            // the test runner happens to embed, which keeps the test
            // environment-agnostic.
            var testAssembly = typeof(VersionFileReaderTest).Assembly.Location;
            var expectedInfo = FileVersionInfo.GetVersionInfo(testAssembly);

            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var pluginsDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer", "Plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(testAssembly, Path.Combine(pluginsDir, "LmpClient.dll"));

                var meta = VersionFileReader.ReadFromInstalledDll(kspRoot);

                Assert.IsNotNull(meta);
                // The metadata's MAJOR/MINOR/PATCH should equal whatever
                // the test DLL embeds (whether via InformationalVersion
                // parse or FileVersion synthesis).
                Assert.AreEqual(expectedInfo.FileMajorPart, meta!.Major);
                Assert.AreEqual(expectedInfo.FileMinorPart, meta.Minor);
                Assert.AreEqual(expectedInfo.FileBuildPart, meta.Patch);
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        // --- ReadInstalledVersion orchestration with DLL fallback ---

        [TestMethod]
        public void ReadInstalledVersion_LegacyJsonOnly_FallsBackToLegacySynthesis()
        {
            // No DLL planted. Legacy-shape JSON synthesises v0.29.1 — the
            // "stale upstream file with no LMP DLLs" install. Operator UX
            // is still correct: cross-channel pre-flight sees "stable
            // 0.29.1" and warns when switching to per-agency-private.
            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var versionDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer");
                Directory.CreateDirectory(versionDir);
                File.WriteAllText(
                    Path.Combine(versionDir, "LunaMultiplayer.version"),
                    "{ \"VERSION\": { \"MAJOR\": 0, \"MINOR\": 29, \"PATCH\": 1 } }");

                var meta = VersionFileReader.ReadInstalledVersion(kspRoot);

                Assert.IsNotNull(meta);
                Assert.AreEqual("v0.29.1", meta!.Tag);
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadInstalledVersion_LegacyJsonPlusDll_PrefersDll()
        {
            // THE RESCUE CASE: legacy upstream JSON (says v0.29.1) +
            // current DLL. Without the fallback the operator sees
            // misdetected v0.29.1; with the fallback we read the DLL's
            // embedded version instead.
            var testAssembly = typeof(VersionFileReaderTest).Assembly.Location;
            var expectedInfo = FileVersionInfo.GetVersionInfo(testAssembly);

            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var lmpDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer");
                Directory.CreateDirectory(lmpDir);
                // Legacy shape — no TAG field.
                File.WriteAllText(
                    Path.Combine(lmpDir, "LunaMultiplayer.version"),
                    "{ \"VERSION\": { \"MAJOR\": 0, \"MINOR\": 29, \"PATCH\": 1 } }");
                // Plant a DLL.
                var pluginsDir = Path.Combine(lmpDir, "Plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(testAssembly, Path.Combine(pluginsDir, "LmpClient.dll"));

                var meta = VersionFileReader.ReadInstalledVersion(kspRoot);

                Assert.IsNotNull(meta);
                // DLL won — the metadata reflects the DLL's embedded version,
                // not the stale JSON's 0.29.1.
                Assert.AreEqual(expectedInfo.FileMajorPart, meta!.Major);
                Assert.AreEqual(expectedInfo.FileMinorPart, meta.Minor);
                Assert.AreEqual(expectedInfo.FileBuildPart, meta.Patch);
                Assert.AreNotEqual(29, meta.Minor,
                    "The DLL should have rescued the orchestrator from the stale-JSON 0.29.1 result.");
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadInstalledVersion_NoJsonButDllPresent_UsesDll()
        {
            // Install with DLLs but the .version file is entirely missing —
            // e.g. an operator who hand-dropped a plugin folder.
            var testAssembly = typeof(VersionFileReaderTest).Assembly.Location;
            var expectedInfo = FileVersionInfo.GetVersionInfo(testAssembly);

            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var pluginsDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer", "Plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(testAssembly, Path.Combine(pluginsDir, "LmpClient.dll"));

                var meta = VersionFileReader.ReadInstalledVersion(kspRoot);

                Assert.IsNotNull(meta);
                Assert.AreEqual(expectedInfo.FileMajorPart, meta!.Major);
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadInstalledVersion_AuthoritativeJsonPlusDll_PrefersJson()
        {
            // Piece-C+ JSON beats the DLL — the JSON carries channel +
            // revision unambiguously, which the DLL's FileVersion alone
            // cannot. Verifies the DLL fallback does NOT kick in for
            // normal installs.
            var testAssembly = typeof(VersionFileReaderTest).Assembly.Location;
            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var lmpDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer");
                Directory.CreateDirectory(lmpDir);
                File.WriteAllText(
                    Path.Combine(lmpDir, "LunaMultiplayer.version"),
                    "{ \"TAG\": \"v0.31.0-per-agency-private-7\", " +
                    "\"VERSION\": { \"MAJOR\": 0, \"MINOR\": 31, \"PATCH\": 0 } }");
                var pluginsDir = Path.Combine(lmpDir, "Plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(testAssembly, Path.Combine(pluginsDir, "LmpClient.dll"));

                var meta = VersionFileReader.ReadInstalledVersion(kspRoot);

                Assert.IsNotNull(meta);
                Assert.AreEqual("v0.31.0-per-agency-private-7", meta!.Tag);
                Assert.AreEqual(VersionMetadata.ChannelPerAgencyPrivate, meta.Channel);
                Assert.AreEqual(7, meta.Revision);
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadInstalledVersion_NoJsonNoDll_ReturnsNull()
        {
            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(kspRoot);
                Assert.IsNull(VersionFileReader.ReadInstalledVersion(kspRoot));
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadInstalledVersion_MalformedJsonPlusDll_UsesDll()
        {
            // JSON is present but malformed (corrupted file, partial write).
            // Without the DLL fallback the orchestrator returned null and
            // MainForm displayed "version file present but unreadable —
            // cohort unknown." With the DLL we rescue the detection.
            var testAssembly = typeof(VersionFileReaderTest).Assembly.Location;
            var kspRoot = Path.Combine(Path.GetTempPath(), "lmp-test-" + Path.GetRandomFileName());
            try
            {
                var lmpDir = Path.Combine(kspRoot, "GameData", "LunaMultiplayer");
                Directory.CreateDirectory(lmpDir);
                File.WriteAllText(
                    Path.Combine(lmpDir, "LunaMultiplayer.version"),
                    "{ \"TAG\": \"v0.31.0-per-");  // truncated mid-token
                var pluginsDir = Path.Combine(lmpDir, "Plugins");
                Directory.CreateDirectory(pluginsDir);
                File.Copy(testAssembly, Path.Combine(pluginsDir, "LmpClient.dll"));

                var meta = VersionFileReader.ReadInstalledVersion(kspRoot);

                Assert.IsNotNull(meta, "DLL should rescue detection when JSON is corrupt.");
            }
            finally
            {
                if (Directory.Exists(kspRoot)) Directory.Delete(kspRoot, recursive: true);
            }
        }
    }
}
