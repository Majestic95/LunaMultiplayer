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

                var agency = ResolveAgencyDecoration(vessel);
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

            var agency = ResolveAgencyDecoration(vessel);
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

            var agency = ResolveAgencyDecoration(widget.vessel);
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
        //
        // Stage 6 Phase 6.6 — when the agency resolves AND the vessel is
        // currently owned by a foreign agency AND has a foreign-crew-count
        // snapshot recorded by VesselLoader.ScrubInvalidProtoCrew (i.e., this
        // is a foreign-agency crewed vessel whose kerbals don't resolve in the
        // local CrewRoster), enrich the agency-only decoration with the
        // pre-scrub crew count via FormatForeignVesselCrewLabel. The IsForeignVessel
        // check at render time is the source of truth — a vessel transferred
        // X → local mid-session still has a stale ForeignCrewCount entry until
        // the next destructive reload, but IsForeignVessel returns false so
        // the label correctly renders as bare [Local Inc.] not the stale
        // [Crew: 3 (Acme)]. Owner-agency vessels, Unassigned-sentinel vessels,
        // and crewless foreign vessels all render the existing 5.18c
        // agency-only decoration unchanged. The returned string is the
        // bracket/newline inner text — callers wrap per surface (\n prefix
        // for flight + map labels, [...] for the tracking-station row widget)
        // so all three surfaces stay consistent with their pre-Phase-6.6 framing.
        private static string ResolveAgencyDecoration(Vessel vessel)
        {
            var agencyLabel = ResolveAgencyLabel(vessel);
            if (string.IsNullOrEmpty(agencyLabel)) return null;

            var agencySystem = AgencySystem.Singleton;

            // Enrich only foreign-agency vessels with crew count. Local-agency
            // and Unassigned vessels render the baseline agency-only label;
            // KSP's local GetCrewCount is authoritative for them. The
            // IsForeignVessel predicate is the render-time source of truth so a
            // stale ForeignCrewCount entry on a transferred-to-local vessel
            // cannot misattribute crew to the local agency.
            if (!AgencyLabelFormatter.IsForeignVessel(vessel.id, agencySystem.LocalAgencyId, agencySystem.VesselOwnership))
                return agencyLabel;

            if (agencySystem.TryGetForeignCrewCount(vessel.id, out var crewCount))
            {
                var crewLabel = AgencyLabelFormatter.FormatForeignVesselCrewLabel(crewCount, agencyLabel);
                if (!string.IsNullOrEmpty(crewLabel)) return crewLabel;
            }

            return agencyLabel;
        }

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
