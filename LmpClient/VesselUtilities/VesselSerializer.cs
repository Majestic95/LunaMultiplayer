using LmpClient.Extensions;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using System;

namespace LmpClient.VesselUtilities
{
    public class VesselSerializer
    {
        /// <summary>
        /// Deserialize a byte array into a protovessel
        /// </summary>
        public static ProtoVessel DeserializeVessel(byte[] data, int numBytes)
        {
            try
            {
                var vesselNode = data.DeserializeToConfigNode(numBytes);
                var configGuid = vesselNode?.GetValue("pid");

                return CreateSafeProtoVesselFromConfigNode(vesselNode, new Guid(configGuid));
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error while deserializing vessel: {e}");
                return null;
            }
        }

        /// <summary>
        /// Serialize a protovessel into a byte array
        /// </summary>
        public static byte[] SerializeVessel(ProtoVessel protoVessel)
        {
            return PreSerializationChecks(protoVessel, out var configNode) ? configNode.Serialize() : new byte[0];
        }

        /// <summary>
        /// Serializes a vessel to a previous preallocated array (avoids garbage generation)
        /// </summary>
        public static void SerializeVesselToArray(ProtoVessel protoVessel, byte[] data, out int numBytes)
        {
            if (PreSerializationChecks(protoVessel, out var configNode))
            {
                configNode.SerializeToArray(data, out numBytes);
            }
            else
            {
                numBytes = 0;
            }
        }

        /// <summary>
        /// Creates a protovessel from a ConfigNode
        /// </summary>
        public static ProtoVessel CreateSafeProtoVesselFromConfigNode(ConfigNode inputNode, Guid protoVesselId)
        {
            try
            {
                //Cannot create a protovessel if HighLogic.CurrentGame is null as we don't have a CrewRoster
                //and the protopartsnapshot constructor needs it
                if (HighLogic.CurrentGame == null)
                    return null;

                //Cannot reuse the Protovessel to save memory garbage as it does not have any clear method :(
                return new ProtoVessel(inputNode, HighLogic.CurrentGame);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Damaged vessel {protoVesselId}, exception: {e}");
                return null;
            }
        }

        #region Private methods

        private static bool PreSerializationChecks(ProtoVessel protoVessel, out ConfigNode configNode)
        {
            configNode = new ConfigNode();

            if (protoVessel == null)
            {
                LunaLog.LogError("[LMP]: Cannot serialize a null protovessel");
                return false;
            }

            try
            {
                protoVessel.Save(configNode);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error while saving vessel: {e}");
                return false;
            }

            var vesselId = new Guid(configNode.GetValue("pid"));

            // [Mod-compat S6] Re-inject lmpOwningAgency stripped by ProtoVessel.Save.
            // ProtoVessel.Save only writes KSP-known fields, so the unknown top-level
            // lmpOwningAgency that VesselDataUpdater wrote into the canonical store
            // never makes it back out through this path — every owner resend would
            // otherwise arrive at peer clients with the field absent, leaving their
            // AgencySystem.VesselOwnership mirrors stuck on the previously-recorded
            // value until the next VesselSync. We only re-assert when the local
            // mirror knows the vessel's real (non-Empty) agency — never originate
            // a stamp from LocalAgencyId, because the relay path would then become
            // a peer-mirror corruption vector under reconnect / transferagency-lag
            // / pre-0.31-Unassigned races. See AgencyMembership.DetermineOutboundStamp
            // XML for the three races and why the LocalAgencyId fallback is
            // intentionally excluded.
            var vesselKnownToMirror = AgencySystem.Singleton.TryGetOwningAgency(vesselId, out var mirrorStampedAgencyId);
            var stampAgencyId = AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: vesselKnownToMirror,
                mirrorStampedAgencyId: mirrorStampedAgencyId,
                perAgencyEnabledClientGate: SettingsSystem.ServerSettings.PerAgencyCareerEnabled);

            if (stampAgencyId != Guid.Empty)
            {
                var stampValue = stampAgencyId.ToString("N");
                if (configNode.HasValue("lmpOwningAgency"))
                    configNode.SetValue("lmpOwningAgency", stampValue);
                else
                    configNode.AddValue("lmpOwningAgency", stampValue);
            }

            //Defend against NaN orbits
            if (configNode.VesselHasNaNPosition())
            {
                LunaLog.LogError($"[LMP]: Vessel {vesselId} has NaN position");
                return false;
            }

            return true;
        }

        #endregion
    }
}
