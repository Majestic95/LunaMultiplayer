using System.IO;
using System.Linq;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class KspDetectorTest
    {
        // libraryfolders.vdf shape matching the operator's actual file (verified
        // 2026-05-20). Two libraries; KSP appid 220200 is in the second one.
        private const string V2VdfWithKsp = """
"libraryfolders"
{
	"0"
	{
		"path"		"C:\\Program Files (x86)\\Steam"
		"label"		""
		"apps"
		{
			"228980"		"522192588"
			"632360"		"3208983811"
		}
	}
	"1"
	{
		"path"		"F:\\SteamLibrary"
		"label"		""
		"apps"
		{
			"220200"		"5911193735"
			"275850"		"28893123846"
		}
	}
}
""";

        // --- ParseSteamLibraryFolders ---

        [TestMethod]
        public void ParseSteamLibraryFolders_V2WithKsp_ReturnsBothLibrariesWithAppIds()
        {
            var libs = KspDetector.ParseSteamLibraryFolders(V2VdfWithKsp);

            Assert.AreEqual(2, libs.Count);
            Assert.AreEqual(@"C:\Program Files (x86)\Steam", libs[0].Path);
            Assert.AreEqual(@"F:\SteamLibrary", libs[1].Path);

            CollectionAssert.AreEqual(new[] { 228980, 632360 }, libs[0].AppIds.ToList());
            CollectionAssert.AreEqual(new[] { 220200, 275850 }, libs[1].AppIds.ToList());
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_KspAppIdPresent_OnlyMatchedInSecondLibrary()
        {
            // Defensive: confirm the parser correctly identifies WHICH library
            // owns the KSP appid. Without this assertion, a parser bug that
            // assigned every appid to the first library would still report
            // "found KSP" and the wrong path would be returned.
            var libs = KspDetector.ParseSteamLibraryFolders(V2VdfWithKsp);

            Assert.IsFalse(libs[0].AppIds.Contains(KspDetector.KspSteamAppId));
            Assert.IsTrue(libs[1].AppIds.Contains(KspDetector.KspSteamAppId));
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_EmptyInput_ReturnsEmpty()
        {
            Assert.AreEqual(0, KspDetector.ParseSteamLibraryFolders("").Count);
            Assert.AreEqual(0, KspDetector.ParseSteamLibraryFolders("   \n\t").Count);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_NullInput_ReturnsEmpty()
        {
            Assert.AreEqual(0, KspDetector.ParseSteamLibraryFolders(null!).Count);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_MalformedNoClosingBrace_ReturnsWhateverWasParsed()
        {
            // Truncated input — parser should not throw, just bail when the
            // brace depth never returns to zero.
            const string malformed = """
"libraryfolders"
{
	"0"
	{
		"path"		"C:\\foo"
""";
            var libs = KspDetector.ParseSteamLibraryFolders(malformed);
            // Parser bailed before recording the entry — expected.
            Assert.AreEqual(0, libs.Count);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_PathWithEscapedQuotes_Unescaped()
        {
            // Steam doesn't actually do this, but the parser supports \" → ".
            const string vdf = """
"libraryfolders"
{
	"0"
	{
		"path"		"D:\\Games\\Library With \"Quotes\""
	}
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(1, libs.Count);
            Assert.AreEqual(@"D:\Games\Library With ""Quotes""", libs[0].Path);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_LibraryWithoutAppsBlock_HasEmptyAppIds()
        {
            const string vdf = """
"libraryfolders"
{
	"0"
	{
		"path"		"D:\\NoApps"
	}
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(1, libs.Count);
            Assert.AreEqual(0, libs[0].AppIds.Count);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_LibraryWithoutPath_Skipped()
        {
            // Library block missing the path field — parser silently drops it.
            const string vdf = """
"libraryfolders"
{
	"0"
	{
		"label"		""
	}
	"1"
	{
		"path"		"E:\\Real"
	}
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(1, libs.Count);
            Assert.AreEqual(@"E:\Real", libs[0].Path);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_NestedBlockInsideApps_DoesNotProduceFalseAppId()
        {
            // Defensive: if Steam ever reformats apps with nested objects per
            // appid (e.g. installation metadata sub-blocks), the parser must
            // NOT treat the nested object's keys as appids.
            const string vdf = """
"libraryfolders"
{
	"0"
	{
		"path"		"C:\\Nested"
		"apps"
		{
			"220200"
			{
				"meta"		"value"
			}
		}
	}
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(1, libs.Count);
            // The "220200" key is followed by a brace, not a value — current
            // parser correctly skips it; appid list is empty.
            Assert.AreEqual(0, libs[0].AppIds.Count);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_PathAfterApps_StillExtractsPath()
        {
            // VDF key order is not specified by the format. Parser must
            // dispatch on key name, not position.
            const string vdf = """
"libraryfolders"
{
	"0"
	{
		"apps"
		{
			"220200"		"1"
		}
		"path"		"D:\\PathAfterApps"
	}
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(1, libs.Count);
            Assert.AreEqual(@"D:\PathAfterApps", libs[0].Path);
            CollectionAssert.AreEqual(new[] { 220200 }, libs[0].AppIds.ToList());
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_EmptyAppsBlock_HasEmptyAppIds()
        {
            const string vdf = """
"libraryfolders"
{
	"0"
	{
		"path"		"E:\\NoAppIds"
		"apps"		{ }
	}
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(1, libs.Count);
            Assert.AreEqual(0, libs[0].AppIds.Count);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_DuplicatePathsInSeparateLibraries_BothEmittedDeduplicatedByConsumer()
        {
            // The parser itself does NOT dedupe — the consumer's HashSet
            // does. This pins that contract so a future "smart" dedup inside
            // ParseSteamLibraryFolders doesn't silently change behaviour.
            const string vdf = """
"libraryfolders"
{
	"0" { "path" "F:\\Dup" }
	"1" { "path" "F:\\Dup" }
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(2, libs.Count);
            Assert.AreEqual(libs[0].Path, libs[1].Path);
        }

        [TestMethod]
        public void ParseSteamLibraryFolders_WithoutTopLevelWrapper_StillParses()
        {
            // Some historical Steam versions wrote the inner block without the
            // "libraryfolders" wrapper. Tolerate it.
            const string vdf = """
"0"
{
	"path"		"C:\\NoWrapper"
}
""";
            var libs = KspDetector.ParseSteamLibraryFolders(vdf);
            Assert.AreEqual(1, libs.Count);
            Assert.AreEqual(@"C:\NoWrapper", libs[0].Path);
        }

        // --- ValidateKspPath ---

        [TestMethod]
        public void ValidateKspPath_Null_ReturnsFalse() => Assert.IsFalse(KspDetector.ValidateKspPath(null));

        [TestMethod]
        public void ValidateKspPath_Empty_ReturnsFalse() => Assert.IsFalse(KspDetector.ValidateKspPath(""));

        [TestMethod]
        public void ValidateKspPath_Whitespace_ReturnsFalse() => Assert.IsFalse(KspDetector.ValidateKspPath("   "));

        [TestMethod]
        public void ValidateKspPath_NonExistentPath_ReturnsFalse()
        {
            var phantom = Path.Combine(Path.GetTempPath(), "lmp-test-phantom-" + Path.GetRandomFileName());
            Assert.IsFalse(KspDetector.ValidateKspPath(phantom));
        }

        [TestMethod]
        public void ValidateKspPath_DirectoryWithoutSquadSubdir_ReturnsFalse()
        {
            var fakeKsp = Path.Combine(Path.GetTempPath(), "lmp-test-ksp-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(fakeKsp);
                // No GameData/Squad subdir — should fail validation.
                Assert.IsFalse(KspDetector.ValidateKspPath(fakeKsp));
            }
            finally
            {
                if (Directory.Exists(fakeKsp)) Directory.Delete(fakeKsp, recursive: true);
            }
        }

        [TestMethod]
        public void ValidateKspPath_DirectoryWithSquadSubdir_ReturnsTrue()
        {
            var fakeKsp = Path.Combine(Path.GetTempPath(), "lmp-test-ksp-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(fakeKsp, "GameData", "Squad"));
                Assert.IsTrue(KspDetector.ValidateKspPath(fakeKsp));
            }
            finally
            {
                if (Directory.Exists(fakeKsp)) Directory.Delete(fakeKsp, recursive: true);
            }
        }

        [TestMethod]
        public void ValidateKspPath_InvalidPathChars_ReturnsFalse()
        {
            // Path containing characters that aren't legal on Windows.
            Assert.IsFalse(KspDetector.ValidateKspPath("C:\\Foo<>\\Bar"));
        }

        [TestMethod]
        public void ValidateKspPath_TrailingDotSegments_NormalisedBeforeProbe()
        {
            // Operator pastes a path with ".." segments — Path.GetFullPath
            // canonicalises it before the directory probe. If the resolved
            // path lands on a real install, accept; otherwise reject.
            var realKsp = Path.Combine(Path.GetTempPath(), "lmp-test-norm-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(realKsp, "GameData", "Squad"));

                // Build a path with a redundant .. segment that resolves back
                // to realKsp. Path.GetFullPath strips the .. correctly.
                var dotted = Path.Combine(realKsp, "x", "..");
                Assert.IsTrue(KspDetector.ValidateKspPath(dotted));
            }
            finally
            {
                if (Directory.Exists(realKsp)) Directory.Delete(realKsp, recursive: true);
            }
        }

        // --- ResolveKspUnderSteamLibrary ---

        [TestMethod]
        public void ResolveKspUnderSteamLibrary_LibraryWithKsp_ReturnsCanonicalKspPath()
        {
            var libraryRoot = Path.Combine(Path.GetTempPath(), "lmp-test-lib-" + Path.GetRandomFileName());
            try
            {
                var kspPath = Path.Combine(libraryRoot, "steamapps", "common", "Kerbal Space Program");
                Directory.CreateDirectory(Path.Combine(kspPath, "GameData", "Squad"));

                var resolved = KspDetector.ResolveKspUnderSteamLibrary(libraryRoot);
                Assert.AreEqual(kspPath, resolved);
            }
            finally
            {
                if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ResolveKspUnderSteamLibrary_LibraryWithoutKsp_ReturnsNull()
        {
            var libraryRoot = Path.Combine(Path.GetTempPath(), "lmp-test-emptylib-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(libraryRoot);
                Assert.IsNull(KspDetector.ResolveKspUnderSteamLibrary(libraryRoot));
            }
            finally
            {
                if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ResolveKspUnderSteamLibrary_NullPath_ReturnsNull()
        {
            Assert.IsNull(KspDetector.ResolveKspUnderSteamLibrary(null!));
        }

        // --- EnumerateCandidates ---

        [TestMethod]
        public void EnumerateCandidates_LastUsedPathValid_YieldedFirst()
        {
            var lastUsed = Path.Combine(Path.GetTempPath(), "lmp-test-lastused-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(lastUsed, "GameData", "Squad"));
                var first = KspDetector.EnumerateCandidates(lastUsed).FirstOrDefault();
                Assert.AreEqual(lastUsed, first);
            }
            finally
            {
                if (Directory.Exists(lastUsed)) Directory.Delete(lastUsed, recursive: true);
            }
        }

        [TestMethod]
        public void EnumerateCandidates_LastUsedPathInvalid_NotYielded()
        {
            var fake = Path.Combine(Path.GetTempPath(), "lmp-test-bad-" + Path.GetRandomFileName());
            // Don't create the directory — invalid candidate.
            foreach (var path in KspDetector.EnumerateCandidates(fake))
            {
                Assert.AreNotEqual(fake, path);
            }
        }

        [TestMethod]
        public void EnumerateCandidates_NullLastUsed_DoesNotThrow()
        {
            // Real Steam/CKAN/GOG values may yield real paths on this machine;
            // we just assert the call doesn't throw.
            var _ = KspDetector.EnumerateCandidates(null).ToList();
        }

        // --- EnumerateSteamKspPaths ---

        [TestMethod]
        public void EnumerateSteamKspPaths_NullSteamRoot_YieldsNothing()
        {
            Assert.AreEqual(0, KspDetector.EnumerateSteamKspPaths(null).Count());
        }

        [TestMethod]
        public void EnumerateSteamKspPaths_SteamRootWithoutVdf_YieldsNothing()
        {
            var emptySteam = Path.Combine(Path.GetTempPath(), "lmp-test-steam-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(emptySteam);
                Assert.AreEqual(0, KspDetector.EnumerateSteamKspPaths(emptySteam).Count());
            }
            finally
            {
                if (Directory.Exists(emptySteam)) Directory.Delete(emptySteam, recursive: true);
            }
        }

        [TestMethod]
        public void EnumerateSteamKspPaths_VdfPlantedWithKspInstalled_YieldsResolvedPath()
        {
            var steamRoot = Path.Combine(Path.GetTempPath(), "lmp-test-steam-" + Path.GetRandomFileName());
            try
            {
                var steamapps = Path.Combine(steamRoot, "steamapps");
                Directory.CreateDirectory(steamapps);

                // Plant a single-library VDF pointing at a synthesised KSP install.
                var libraryRoot = steamRoot; // KSP under steamRoot/steamapps/common/...
                Directory.CreateDirectory(Path.Combine(libraryRoot, "steamapps", "common", "Kerbal Space Program", "GameData", "Squad"));

                var vdf = $$"""
"libraryfolders"
{
	"0"
	{
		"path"		"{{libraryRoot.Replace("\\", "\\\\")}}"
		"apps"
		{
			"220200"		"123"
		}
	}
}
""";
                File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), vdf);

                var resolved = KspDetector.EnumerateSteamKspPaths(steamRoot).ToList();
                Assert.AreEqual(1, resolved.Count);
                Assert.IsTrue(KspDetector.ValidateKspPath(resolved[0]));
            }
            finally
            {
                if (Directory.Exists(steamRoot)) Directory.Delete(steamRoot, recursive: true);
            }
        }

        [TestMethod]
        public void EnumerateSteamKspPaths_LibraryWithoutKspAppId_StillProbed()
        {
            // A library whose apps block does NOT include 220200 is STILL probed
            // — Steam's apps cache can lag behind manual install relocations, and
            // ValidateKspPath is the final gate. The per-library Directory.Exists
            // cost is microseconds, far cheaper than the false-negative cost of
            // making the operator hand-pick.
            var steamRoot = Path.Combine(Path.GetTempPath(), "lmp-test-steamprobe-" + Path.GetRandomFileName());
            try
            {
                var steamapps = Path.Combine(steamRoot, "steamapps");
                Directory.CreateDirectory(steamapps);
                Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps", "common", "Kerbal Space Program", "GameData", "Squad"));

                var vdf = $$"""
"libraryfolders"
{
	"0"
	{
		"path"		"{{steamRoot.Replace("\\", "\\\\")}}"
		"apps"
		{
			"999999"		"123"
		}
	}
}
""";
                File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), vdf);

                Assert.AreEqual(1, KspDetector.EnumerateSteamKspPaths(steamRoot).Count());
            }
            finally
            {
                if (Directory.Exists(steamRoot)) Directory.Delete(steamRoot, recursive: true);
            }
        }

        [TestMethod]
        public void EnumerateSteamKspPaths_LibraryWithEmptyAppIds_ProbedAnyway()
        {
            // A library block missing an apps block at all (legacy v1 shape)
            // is still probed — the appid filter is opportunistic, not a hard
            // gate. Empty AppIds.Count signals "unknown contents", probe anyway.
            var steamRoot = Path.Combine(Path.GetTempPath(), "lmp-test-steamlegacy-" + Path.GetRandomFileName());
            try
            {
                var steamapps = Path.Combine(steamRoot, "steamapps");
                Directory.CreateDirectory(steamapps);
                Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps", "common", "Kerbal Space Program", "GameData", "Squad"));

                var vdf = $$"""
"libraryfolders"
{
	"0"
	{
		"path"		"{{steamRoot.Replace("\\", "\\\\")}}"
	}
}
""";
                File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), vdf);

                Assert.AreEqual(1, KspDetector.EnumerateSteamKspPaths(steamRoot).Count());
            }
            finally
            {
                if (Directory.Exists(steamRoot)) Directory.Delete(steamRoot, recursive: true);
            }
        }
    }
}
