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
    }
}
