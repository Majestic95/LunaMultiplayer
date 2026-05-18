using LmpClient.Diagnostics;
using LmpClient.Extensions;
using LmpClient.Systems.Agency;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.Utilities;
using LmpClient.VesselUtilities;
using System;

namespace LmpClient.Systems.VesselProtoSys
{
    public class VesselProto
    {
        public Guid VesselId;
        public byte[] RawData = new byte[0];
        public int NumBytes;
        public double GameTime;
        public bool ForceReload;

        public Vessel LoadVessel()
        {
            return null;
        }

        public ProtoVessel CreateProtoVessel()
        {
            var configNode = RawData.DeserializeToConfigNode(NumBytes);
            if (configNode == null || configNode.VesselHasNaNPosition())
            {
                LunaLog.LogError($"Received a malformed vessel from SERVER. Id {VesselId}");
                VesselSyncDiagnostics.LogDiscarded(VesselId, vesselName: null, parts: -1,
                    reason: configNode == null
                        ? "DeserializeToConfigNode returned null (wire payload was unparseable)"
                        : "ConfigNode contained NaN position values (configNode.VesselHasNaNPosition)");
                VesselRemoveSystem.Singleton.KillVessel(VesselId, true, "Malformed vessel");
                return null;
            }

            // [Stage 5.18b] Record this vessel's owning agency from the wire
            // ConfigNode BEFORE handing off to KSP's ProtoVessel ctor — KSP silently
            // drops the unknown top-level lmpOwningAgency field, so this is the only
            // point on the receive path where the value is reachable. The registry
            // is the client-side mirror of the server's authoritative
            // Vessel.OwningAgencyId (Server/System/Vessel/Classes/Vessel.cs).
            //
            // Relay-safety: this call site sees both authoritative VesselSync replies
            // (which serialize from the server's canonical store via
            // GetVesselInConfigNodeFormat and DO carry lmpOwningAgency) AND relayed
            // protos (server forwards the ORIGINAL sender bytes per the warning at
            // Server/Message/VesselMsgReader.cs:188-198 — those have no
            // lmpOwningAgency because KSP's BackupVessel/Save strips the unknown
            // field on every local-owner resend). RecordOwnership applies the
            // preservation rule: incoming non-Empty wins; incoming Empty inserts
            // only when there's no prior entry, never downgrades a known real id.
            var owningAgency = AgencyMembership.TryParseAgencyId(configNode.GetValue("lmpOwningAgency"));
            AgencyMembership.RecordOwnership(AgencySystem.Singleton?.VesselOwnership, VesselId, owningAgency);

            var newProto = VesselSerializer.CreateSafeProtoVesselFromConfigNode(configNode, VesselId);
            if (newProto == null)
            {
                LunaLog.LogError($"Received a malformed vessel from SERVER. Id {VesselId}");
                VesselSyncDiagnostics.LogDiscarded(VesselId, vesselName: null, parts: -1,
                    reason: "VesselSerializer.CreateSafeProtoVesselFromConfigNode returned null (ProtoVessel ctor refused the node)");
                VesselRemoveSystem.Singleton.KillVessel(VesselId, true, "Malformed vessel");
                return null;
            }

            return newProto;
        }
    }
}
