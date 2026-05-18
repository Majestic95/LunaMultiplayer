using LunaConfigNode;
using Server.Context;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Server.System
{
    /// <summary>
    /// Here we keep a copy of all the player vessels in <see cref="Vessel"/> format and we also save them to files at a specified rate
    /// </summary>
    public static class VesselStoreSystem
    {
        public const string VesselFileFormat = ".txt";
        public static string VesselsPath = Path.Combine(ServerContext.UniverseDirectory, "Vessels");

        public static ConcurrentDictionary<Guid, Vessel.Classes.Vessel> CurrentVessels = new ConcurrentDictionary<Guid, Vessel.Classes.Vessel>();

        private static readonly object BackupLock = new object();

        public static bool VesselExists(Guid vesselId) => CurrentVessels.ContainsKey(vesselId);

        /// <summary>
        /// Removes a vessel from the store
        /// </summary>
        public static void RemoveVessel(Guid vesselId)
        {
            CurrentVessels.TryRemove(vesselId, out _);
            //Retro-review S5: drop the per-vessel object held by VesselDataUpdater so the
            //Semaphore dictionary doesn't grow without bound across the server's lifetime.
            Vessel.VesselDataUpdater.ForgetVessel(vesselId);

            _ = Task.Run(() =>
            {
                lock (BackupLock)
                {
                    FileHandler.FileDelete(Path.Combine(VesselsPath, $"{vesselId}{VesselFileFormat}"));
                }
            });
        }

        /// <summary>
        /// Returns a vessel in the standard KSP format
        /// </summary>
        public static string GetVesselInConfigNodeFormat(Guid vesselId)
        {
            return CurrentVessels.TryGetValue(vesselId, out var vessel) ?
                vessel.ToString() : null;
        }

        /// <summary>
        /// Load the stored vessels into the dictionary
        /// </summary>
        public static void LoadExistingVessels()
        {
            ChangeExistingVesselFormats();
            lock (BackupLock)
            {
                foreach (var file in Directory.GetFiles(VesselsPath).Where(f => Path.GetExtension(f) == VesselFileFormat))
                {
                    if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var vesselId))
                    {
                        CurrentVessels.TryAdd(vesselId, new Vessel.Classes.Vessel(FileHandler.ReadFileText(file)));
                    }
                }
            }

            // [Stage 5.16b — round-5 upgrade-lens review] When per-agency career is on,
            // report how many freshly-loaded vessels carry no lmpOwningAgency (spec §10 Q3
            // "Unassigned sentinel"). Operators upgrading a pre-0.31 universe in place
            // (despite the spec's "fresh-start" recommendation) need to know how many
            // vessels require admin transferagency (Stage 5.18d). Quiet when zero so the
            // common gate-off and fresh-universe boots emit nothing.
            if (Agency.AgencySystem.PerAgencyEnabled)
            {
                var unassigned = CurrentVessels.Values.Count(v => v.OwningAgencyId == Guid.Empty);
                if (unassigned > 0)
                {
                    Log.LunaLog.Normal($"[fix:per-agency-career] {unassigned} pre-existing vessel(s) loaded with no agency assignment (Unassigned sentinel per spec §10 Q3). Use transferagency (Stage 5.18d) to assign owners.");
                }
            }
        }

        /// <summary>
        /// Transform OLD Xml vessels into the new format
        /// TODO: Remove this for next version
        /// </summary>
        public static void ChangeExistingVesselFormats()
        {
            lock (BackupLock)
            {
                foreach (var file in Directory.GetFiles(VesselsPath).Where(f => Path.GetExtension(f) == ".xml"))
                {
                    if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var vesselId))
                    {
                        var vesselAsCfgNode = XmlConverter.ConvertToConfigNode(FileHandler.ReadFileText(file));
                        FileHandler.WriteToFile(file.Replace(".xml", ".txt"), vesselAsCfgNode);
                    }
                    FileHandler.FileDelete(file);
                }
            }
        }

        /// <summary>
        /// Actually performs the backup of the vessels to file
        /// </summary>
        public static void BackupVessels()
        {
            lock (BackupLock)
            {
                var vesselsInCfgNode = CurrentVessels.ToArray();
                foreach (var vessel in vesselsInCfgNode)
                {
                    FileHandler.WriteToFile(Path.Combine(VesselsPath, $"{vessel.Key}{VesselFileFormat}"), vessel.Value.ToString());
                }
            }
        }

        /// <summary>
        /// Writes one vessel to disk so live patches (orbit, IDENT, position fields) are reflected in the Vessels folder without waiting for <see cref="BackupVessels"/>.
        /// </summary>
        public static void PersistVesselToFile(Guid vesselId)
        {
            if (!CurrentVessels.TryGetValue(vesselId, out var vessel)) return;

            lock (BackupLock)
            {
                FileHandler.WriteToFile(Path.Combine(VesselsPath, $"{vesselId}{VesselFileFormat}"), vessel.ToString());
            }
        }
    }
}
