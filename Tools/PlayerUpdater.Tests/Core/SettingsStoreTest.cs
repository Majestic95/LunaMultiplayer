using System.IO;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class SettingsStoreTest
    {
        // --- ReadFromJson ---

        [TestMethod]
        public void ReadFromJson_FullObject_RoundTrips()
        {
            const string json = """
            {
                "LastKspPath": "F:\\SteamLibrary\\steamapps\\common\\Kerbal Space Program",
                "LastChannelPreference": "per-agency-private",
                "BackupRetention": 5
            }
            """;

            var settings = SettingsStore.ReadFromJson(json);

            Assert.AreEqual(@"F:\SteamLibrary\steamapps\common\Kerbal Space Program", settings.LastKspPath);
            Assert.AreEqual("per-agency-private", settings.LastChannelPreference);
            Assert.AreEqual(5, settings.BackupRetention);
        }

        [TestMethod]
        public void ReadFromJson_EmptyObject_ReturnsDefault()
        {
            var settings = SettingsStore.ReadFromJson("{}");

            Assert.IsNull(settings.LastKspPath);
            Assert.IsNull(settings.LastChannelPreference);
            Assert.AreEqual(UpdaterSettings.DefaultBackupRetention, settings.BackupRetention);
        }

        [TestMethod]
        public void ReadFromJson_MissingChannelField_LeavesChannelNull()
        {
            const string json = """
            { "LastKspPath": "C:\\KSP", "BackupRetention": 7 }
            """;

            var settings = SettingsStore.ReadFromJson(json);

            Assert.AreEqual(@"C:\KSP", settings.LastKspPath);
            Assert.IsNull(settings.LastChannelPreference);
            Assert.AreEqual(7, settings.BackupRetention);
        }

        [TestMethod]
        public void ReadFromJson_NegativeRetention_FallsBackToDefault()
        {
            const string json = """
            { "BackupRetention": -3 }
            """;

            var settings = SettingsStore.ReadFromJson(json);

            Assert.AreEqual(UpdaterSettings.DefaultBackupRetention, settings.BackupRetention);
        }

        [TestMethod]
        public void ReadFromJson_RetentionAsString_FallsBackToDefault()
        {
            // Defensive: BackupRetention must be a number, not a numeric string.
            const string json = """
            { "BackupRetention": "5" }
            """;

            var settings = SettingsStore.ReadFromJson(json);

            Assert.AreEqual(UpdaterSettings.DefaultBackupRetention, settings.BackupRetention);
        }

        [TestMethod]
        public void ReadFromJson_PathAsNumber_LeavesPathNull()
        {
            const string json = """
            { "LastKspPath": 42 }
            """;

            var settings = SettingsStore.ReadFromJson(json);

            Assert.IsNull(settings.LastKspPath);
        }

        [TestMethod]
        public void ReadFromJson_Malformed_ReturnsDefault()
        {
            var settings = SettingsStore.ReadFromJson("{ not valid json");

            Assert.AreSame(UpdaterSettings.Default, settings);
        }

        [TestMethod]
        public void ReadFromJson_NonObjectRoot_ReturnsDefault()
        {
            var settings = SettingsStore.ReadFromJson("[1, 2, 3]");

            Assert.AreSame(UpdaterSettings.Default, settings);
        }

        [TestMethod]
        public void ReadFromJson_Empty_ReturnsDefault()
        {
            Assert.AreSame(UpdaterSettings.Default, SettingsStore.ReadFromJson(""));
            Assert.AreSame(UpdaterSettings.Default, SettingsStore.ReadFromJson("   "));
        }

        [TestMethod]
        public void ReadFromJson_EmptyStringPath_TreatedAsNull()
        {
            // An empty-string value should not propagate as a "remembered"
            // path — that would confuse the detection chain.
            const string json = """
            { "LastKspPath": "" }
            """;

            var settings = SettingsStore.ReadFromJson(json);
            Assert.IsNull(settings.LastKspPath);
        }

        // --- WriteToJson + round-trip ---

        [TestMethod]
        public void WriteToJson_AllFieldsPopulated_RoundTripsThroughReadFromJson()
        {
            var original = new UpdaterSettings(@"D:\KSP", VersionMetadata.ChannelPerAgencyPrivate, 4);

            var json = SettingsStore.WriteToJson(original);
            var roundTripped = SettingsStore.ReadFromJson(json);

            Assert.AreEqual(original.LastKspPath, roundTripped.LastKspPath);
            Assert.AreEqual(original.LastChannelPreference, roundTripped.LastChannelPreference);
            Assert.AreEqual(original.BackupRetention, roundTripped.BackupRetention);
        }

        [TestMethod]
        public void WriteToJson_DefaultSettings_RoundTripsCleanly()
        {
            var json = SettingsStore.WriteToJson(UpdaterSettings.Default);
            var roundTripped = SettingsStore.ReadFromJson(json);

            Assert.IsNull(roundTripped.LastKspPath);
            Assert.IsNull(roundTripped.LastChannelPreference);
            Assert.AreEqual(UpdaterSettings.DefaultBackupRetention, roundTripped.BackupRetention);
        }

        [TestMethod]
        public void WriteToJson_Indented_ContainsNewlines()
        {
            // Indented JSON is a contract — operators may hand-edit. A regression
            // to compact JSON should be caught.
            var json = SettingsStore.WriteToJson(UpdaterSettings.Default);

            Assert.IsTrue(json.Contains("\n"), "WriteToJson should produce indented JSON for human-edit");
        }

        // --- WriteSettingsToPath / ReadSettingsFromPath — atomic filesystem ---

        [TestMethod]
        public void WriteSettingsToPath_CreatesDirectoryAndWrites()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "lmp-test-settings-" + Path.GetRandomFileName());
            try
            {
                var settingsPath = Path.Combine(tempRoot, "nested", "settings.json");
                var settings = new UpdaterSettings(@"X:\Foo", "stable", 2);

                var success = SettingsStore.WriteSettingsToPath(settings, settingsPath);

                Assert.IsTrue(success);
                Assert.IsTrue(File.Exists(settingsPath));

                var roundTripped = SettingsStore.ReadSettingsFromPath(settingsPath);
                Assert.AreEqual(@"X:\Foo", roundTripped.LastKspPath);
                Assert.AreEqual("stable", roundTripped.LastChannelPreference);
                Assert.AreEqual(2, roundTripped.BackupRetention);
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
        }

        [TestMethod]
        public void WriteSettingsToPath_OverwritesExistingFile()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "lmp-test-settings-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(tempRoot);
                var settingsPath = Path.Combine(tempRoot, "settings.json");
                File.WriteAllText(settingsPath, """
                { "LastKspPath": "OLD", "BackupRetention": 9 }
                """);

                var success = SettingsStore.WriteSettingsToPath(
                    new UpdaterSettings("NEW", null, 1), settingsPath);

                Assert.IsTrue(success);

                var roundTripped = SettingsStore.ReadSettingsFromPath(settingsPath);
                Assert.AreEqual("NEW", roundTripped.LastKspPath);
                Assert.AreEqual(1, roundTripped.BackupRetention);
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
        }

        [TestMethod]
        public void WriteSettingsToPath_LeavesNoStaleTempFile()
        {
            // Atomic-write contract: after a successful write, no .tmp file lingers.
            var tempRoot = Path.Combine(Path.GetTempPath(), "lmp-test-settings-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(tempRoot);
                var settingsPath = Path.Combine(tempRoot, "settings.json");

                var success = SettingsStore.WriteSettingsToPath(
                    new UpdaterSettings("Y:\\Bar", null, 3), settingsPath);

                Assert.IsTrue(success);
                Assert.IsFalse(File.Exists(settingsPath + ".tmp"));
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
        }

        [TestMethod]
        public void WriteSettings_NullSettings_ReturnsFalse()
        {
            Assert.IsFalse(SettingsStore.WriteSettings(null!));
        }

        [TestMethod]
        public void ReadSettingsFromPath_NonExistentFile_ReturnsDefault()
        {
            var phantom = Path.Combine(Path.GetTempPath(), "lmp-test-phantom-" + Path.GetRandomFileName());
            var settings = SettingsStore.ReadSettingsFromPath(phantom);

            Assert.AreSame(UpdaterSettings.Default, settings);
        }

        [TestMethod]
        public void ReadSettingsFromPath_CanonicalAbsentButTmpPresent_RecoversFromTmp()
        {
            // Power-loss recovery: WriteAllText to .tmp succeeded but
            // File.Move did not. The next read salvages the .tmp content
            // rather than returning Default and silently losing the operator's
            // latest preference.
            var tempRoot = Path.Combine(Path.GetTempPath(), "lmp-test-settings-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(tempRoot);
                var settingsPath = Path.Combine(tempRoot, "settings.json");
                File.WriteAllText(settingsPath + ".tmp", """
                { "LastKspPath": "RECOVERED", "BackupRetention": 7 }
                """);
                Assert.IsFalse(File.Exists(settingsPath));

                var settings = SettingsStore.ReadSettingsFromPath(settingsPath);

                Assert.AreEqual("RECOVERED", settings.LastKspPath);
                Assert.AreEqual(7, settings.BackupRetention);
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadSettingsFromPath_CanonicalAndTmpBothAbsent_ReturnsDefault()
        {
            // The recovery path only kicks in when canonical is missing AND
            // .tmp is present; with neither, return Default normally.
            var tempRoot = Path.Combine(Path.GetTempPath(), "lmp-test-settings-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(tempRoot);
                var settingsPath = Path.Combine(tempRoot, "settings.json");

                var settings = SettingsStore.ReadSettingsFromPath(settingsPath);
                Assert.AreSame(UpdaterSettings.Default, settings);
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
        }

        [TestMethod]
        public void ReadSettingsFromPath_CorruptFile_ReturnsDefaultNotThrow()
        {
            // A corrupt settings.json must not brick the updater. Returning
            // Default is correct — the player loses their last-used path but
            // can re-pick.
            var tempRoot = Path.Combine(Path.GetTempPath(), "lmp-test-settings-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(tempRoot);
                var settingsPath = Path.Combine(tempRoot, "settings.json");
                File.WriteAllText(settingsPath, "{ \"LastKspPath\": ");  // truncated JSON

                var settings = SettingsStore.ReadSettingsFromPath(settingsPath);
                Assert.AreSame(UpdaterSettings.Default, settings);
            }
            finally
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
        }

        // --- Public API smoke ---

        [TestMethod]
        public void ResolveSettingsFilePath_NonNullOnNormalHost()
        {
            // Test host has %LOCALAPPDATA% — the call should resolve a real path.
            var path = SettingsStore.ResolveSettingsFilePath();
            Assert.IsNotNull(path);
            StringAssert.EndsWith(path, "settings.json");
        }

        [TestMethod]
        public void ReadSettings_LiveCall_DoesNotThrow()
        {
            // Smoke: the live read against %LOCALAPPDATA% must never throw,
            // regardless of whether a settings.json exists.
            var _ = SettingsStore.ReadSettings();
        }
    }
}
