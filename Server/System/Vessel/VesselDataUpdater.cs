using LunaConfigNode.CfgNode;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace Server.System.Vessel
{
    /// <summary>
    /// We try to avoid working with protovessels as much as possible as they can be huge files.
    /// This class patches the vessel file with the information messages we receive about a position and other vessel properties.
    /// This way we send the whole vessel definition only when there are parts that have changed 
    /// </summary>
    public partial class VesselDataUpdater
    {
        #region Semaphore

        /// <summary>
        /// To not overwrite our own data we use a lock
        /// </summary>
        private static readonly ConcurrentDictionary<Guid, object> Semaphore = new ConcurrentDictionary<Guid, object>();

        #endregion

        /// <summary>
        /// Sets ORBIT IDENT from the reference body name when provided (e.g. from position or update messages).
        /// </summary>
        internal static void ApplyOrbitIdent(Classes.Vessel vessel, string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return;

            if (vessel.Orbit.Exists("IDENT"))
                vessel.Orbit.Update("IDENT", bodyName);
            else
                vessel.Orbit.Add(new CfgNodeValue<string, string>("IDENT", bodyName));
        }

        /// <summary>
        /// Raw updates a vessel in the dictionary and takes care of the locking in case we received another vessel message type.
        /// <paramref name="clientSubspaceId"/> is stamped onto the vessel as its
        /// <see cref="Classes.Vessel.AuthoritativeSubspaceId"/> (BUG-005/006). The cross-subspace
        /// rejection itself is performed synchronously by <see cref="Server.Message.VesselMsgReader.HandleVesselProto"/>
        /// before this call so that the relay is suppressed on rejection; here we only stamp.
        /// </summary>
        public static void RawConfigNodeInsertOrUpdate(Guid vesselId, string vesselDataInConfigNodeFormat, int clientSubspaceId)
        {
            _ = Task.Run(() =>
            {
                var vessel = new Classes.Vessel(vesselDataInConfigNodeFormat);
                if (GeneralSettings.SettingsStore.ModControl)
                {
                    var vesselParts = vessel.Parts.GetAllValues().Select(p => p.Fields.GetSingle("name").Value);
                    var bannedParts = vesselParts.Except(ModFileSystem.ModControl.AllowedParts);
                    if (bannedParts.Any())
                    {
                        LunaLog.Warning($"Received a vessel with BANNED parts! {vesselId}");
                        return;
                    }
                }
                //BUG-005/006: stamp the contributing client's subspace as the new authority.
                //Sentinels (subspaceId <= 0) are not stamped — they leave existing authority in place
                //so a warping or unidentified client cannot blank a vessel's authority.
                if (clientSubspaceId > 0)
                {
                    vessel.AuthoritativeSubspaceId = clientSubspaceId;
                }
                lock (Semaphore.GetOrAdd(vesselId, new object()))
                {
                    //Preserve authority across reject-and-replace race: if the existing vessel had
                    //higher authority and the incoming update arrived legitimately (already past
                    //the synchronous reject check), keep the more advanced authority.
                    if (clientSubspaceId <= 0
                        && VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var existingForAuth)
                        && existingForAuth.AuthoritativeSubspaceId > 0)
                    {
                        vessel.AuthoritativeSubspaceId = existingForAuth.AuthoritativeSubspaceId;
                    }
                    VesselStoreSystem.CurrentVessels.AddOrUpdate(vesselId, vessel, (key, existingVal) => vessel);
                }
            });
        }
    }
}
