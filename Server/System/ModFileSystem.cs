using LmpCommon.ModFile.Structure;
using LmpCommon.Xml;
using Server.Context;
using Server.Log;
using System;

namespace Server.System
{
    public class ModFileSystem
    {
        public static ModControlStructure ModControl { get; private set; }

        public static void GenerateNewModFile()
        {
            var defaultModFile = new ModControlStructure();
            defaultModFile.SetDefaultAllowedParts();
            defaultModFile.SetDefaultAllowedResources();
            defaultModFile.SetDefaultOptionalPlugins();

            FileHandler.WriteToFile(ServerContext.ModFilePath, LunaXmlSerializer.SerializeToXml(defaultModFile));
        }

        public static void LoadModFile()
        {
            try
            {
                ModControl = LunaXmlSerializer.ReadXmlFromPath<ModControlStructure>(ServerContext.ModFilePath);
            }
            catch (Exception)
            {
                LunaLog.Error("Cannot read LMPModControl file. Will load the default one. Please regenerate it");
                ModControl = new ModControlStructure();
            }
        }

        /// <summary>
        /// Test-only helper. Returns the ModControl reference to its post-boot uninitialised
        /// state (null). MockClientTest disables <c>GeneralSettings.ModControl</c> in the
        /// harness, but a test that accidentally flips it back on would silently filter
        /// against whatever stale allowlist a prior test loaded. This reset forces a
        /// deterministic NRE in that case so the misconfiguration surfaces immediately.
        /// Never call from production code.
        /// </summary>
        internal static void Reset() => ModControl = null;
    }
}