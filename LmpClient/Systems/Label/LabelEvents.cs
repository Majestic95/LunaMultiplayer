using KSP.UI.Screens;
using KSP.UI.Screens.Mapview;
using LmpClient.Base;
using LmpClient.Systems.Agency;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using System;

namespace LmpClient.Systems.Label
{
    public class LabelEvents : SubSystem<LabelSystem>
    {
        public void OnLabelProcessed(BaseLabel label)
        {
            if (label is VesselLabel vesselLabel)
            {
                var vessel = vesselLabel.vessel;
                var owner = LockSystem.LockQuery.GetControlLockOwner(vessel.id);

                if (!string.IsNullOrEmpty(owner))
                    label.text.text = $"{owner}\n{label.text.text}";

                var agency = ResolveAgencyLabel(vessel);
                if (!string.IsNullOrEmpty(agency))
                    label.text.text = $"{label.text.text}\n{agency}";
            }
        }

        public void OnMapLabelProcessed(Vessel vessel, MapNode.CaptionData label)
        {
            if (vessel == null) return;

            var owner = LockSystem.LockQuery.GetControlLockOwner(vessel.id);
            if (!string.IsNullOrEmpty(owner))
            {
                label.Header = $"{owner}\n{label.Header}";
            }

            var agency = ResolveAgencyLabel(vessel);
            if (!string.IsNullOrEmpty(agency))
            {
                label.Header = $"{label.Header}\n{agency}";
            }
        }

        public void OnMapWidgetTextProcessed(TrackingStationWidget widget)
        {
            if (widget.vessel == null) return;

            var owner = LockSystem.LockQuery.GetControlLockOwner(widget.vessel.id);
            if (!string.IsNullOrEmpty(owner))
            {
                widget.textName.text = $"({owner}) {widget.textName.text}";
            }

            var agency = ResolveAgencyLabel(widget.vessel);
            if (!string.IsNullOrEmpty(agency))
            {
                widget.textName.text = $"{widget.textName.text} [{agency}]";
            }
        }

        // Stage 5.18c per-agency UI augmentation. Gated on the server-supplied
        // PerAgencyCareerEnabled flag + a non-empty LocalAgencyId (handshake
        // completed) so shared-agency mode runs unchanged and the per-agency
        // mid-handshake race window doesn't render premature labels. The
        // formatter returns null for vessels absent from the ownership registry
        // (loading state); callers no-op on null to keep the baseline label
        // intact rather than decorating with a misleading "Unknown" badge during
        // initial sync.
        private static string ResolveAgencyLabel(Vessel vessel)
        {
            if (vessel == null) return null;
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return null;
            var agencySystem = AgencySystem.Singleton;
            if (agencySystem.LocalAgencyId == Guid.Empty) return null;

            return AgencyLabelFormatter.FormatVesselAgencyLabel(
                vessel.id,
                agencySystem.LocalAgencyId,
                agencySystem.LocalAgencyDisplayName,
                agencySystem.VesselOwnership,
                agencySystem.OtherAgencies);
        }
    }
}
