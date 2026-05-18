using LmpCommon.Message.Data.Agency;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Pure decision helper for Stage 5.18c tracking-station / map / flight agency
    /// labels. Same testability pattern as <see cref="AgencyMembership"/> — instance
    /// call sites in <c>LmpClient/Systems/Label/LabelEvents</c> reduce to a one-line
    /// delegate; the format math lives here and is unit-tested in <c>LmpClientTest</c>
    /// without needing KSP DLLs loaded.
    ///
    /// <para><b>Label states (per <see cref="AgencySystem.VesselOwnership"/> XML two-
    /// state sentinel).</b>
    /// <list type="bullet">
    ///   <item><b>Vessel absent from ownership registry:</b> return null — the vessel
    ///         hasn't arrived through a wire round yet, or its proto was relay-
    ///         stripped of <c>lmpOwningAgency</c> on first sight. Label baseline
    ///         renders unaugmented; no misleading "Unknown" decoration during
    ///         initial sync. Same fallback the 5.18b XML prescribes.</item>
    ///   <item><b>Registry hit, value = <see cref="Guid.Empty"/>:</b> "Unassigned" —
    ///         spec §10 Q3 sentinel. Any agency may interact pending Stage 5.18d
    ///         <c>transferagency</c>.</item>
    ///   <item><b>Registry hit, value = local agency id:</b> use the local agency's
    ///         display name. Empty display name (mid-handshake race window described
    ///         in <see cref="AgencySystem.LocalAgencyDisplayName"/> XML) falls back
    ///         to "Your Agency" — bounded to one Lidgren message cycle in practice.</item>
    ///   <item><b>Registry hit, value = known other agency id:</b> use that agency's
    ///         <see cref="AgencyInfo.DisplayName"/>. Empty display name on an
    ///         <see cref="AgencyInfo"/> shouldn't happen (the server populates it
    ///         from <c>AgencyState.DisplayName</c>) but is defensively handled by
    ///         falling back to <see cref="AgencyInfo.OwningPlayerName"/> then to
    ///         "Unknown Agency".</item>
    ///   <item><b>Registry hit, value = unknown other agency id:</b> "Unknown Agency"
    ///         — owning agency came online AFTER this client connected (5.18a
    ///         <see cref="AgencySystem.OtherAgencies"/> is a one-shot snapshot at
    ///         handshake time). Stage 5.18d <c>AgencyVisibilityMsgData</c> will
    ///         close this gap with incremental updates.</item>
    /// </list></para>
    ///
    /// <para><b>Gate.</b> Callers must check <c>SettingsSystem.ServerSettings.
    /// PerAgencyCareerEnabled</c> AND <see cref="AgencySystem.LocalAgencyId"/> non-
    /// empty before invoking — this helper does NOT re-check the gate, to keep the
    /// LabelEvents hot path's branch count minimal (called every map widget Update
    /// tick, and every vessel-label render).</para>
    /// </summary>
    public static class AgencyLabelFormatter
    {
        public const string UnassignedLabel = "Unassigned";
        public const string UnknownAgencyLabel = "Unknown Agency";
        public const string LocalFallbackLabel = "Your Agency";

        /// <summary>
        /// Returns the human-readable agency name for the given vessel, or null if
        /// the vessel is absent from the ownership registry (caller leaves the
        /// baseline label unaugmented).
        /// </summary>
        public static string FormatVesselAgencyLabel(
            Guid vesselId,
            Guid localAgencyId,
            string localAgencyDisplayName,
            ConcurrentDictionary<Guid, Guid> vesselOwnership,
            IReadOnlyDictionary<Guid, AgencyInfo> otherAgencies)
        {
            if (vesselOwnership == null) return null;
            if (!vesselOwnership.TryGetValue(vesselId, out var owningAgencyId)) return null;

            if (owningAgencyId == Guid.Empty) return UnassignedLabel;

            if (owningAgencyId == localAgencyId)
            {
                return string.IsNullOrEmpty(localAgencyDisplayName)
                    ? LocalFallbackLabel
                    : localAgencyDisplayName;
            }

            if (otherAgencies != null && otherAgencies.TryGetValue(owningAgencyId, out var info) && info != null)
            {
                if (!string.IsNullOrEmpty(info.DisplayName)) return info.DisplayName;
                if (!string.IsNullOrEmpty(info.OwningPlayerName)) return info.OwningPlayerName;
            }

            return UnknownAgencyLabel;
        }

        /// <summary>
        /// True when the player's local agency still carries the auto-registered
        /// default name <c>"{playerName} Space Agency"</c> from
        /// <c>Server/System/Agency/AgencySystem.RegisterAgency</c>. The Stage 5.18c
        /// <c>AgencyWindow</c> uses this to render a soft "Rename your agency?" hint
        /// next to the rename button — without the hint the rename UX is discoverable
        /// only by reading the label closely. Returns false when either the display
        /// name or owning-player name is missing (mid-handshake race window) so the
        /// hint never fires spuriously.
        /// </summary>
        public static bool IsDefaultDisplayName(string displayName, string owningPlayerName)
        {
            if (string.IsNullOrEmpty(displayName)) return false;
            if (string.IsNullOrEmpty(owningPlayerName)) return false;
            return displayName == owningPlayerName + " Space Agency";
        }
    }
}
