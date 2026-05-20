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

        /// <summary>
        /// Stage 6 Phase 6.6 — foreign-vessel predicate for the
        /// <see cref="Label.LabelEvents"/> crew-count enrichment path. Returns true
        /// when (a) the registry knows the vessel's owning agency, (b) the owning
        /// agency is non-Empty (Unassigned vessels per spec §10 Q3 are NOT
        /// "foreign" — their label decoration is "Unassigned" with no agency to
        /// attribute crew to), and (c) the owning agency differs from the local
        /// player's. Otherwise false — caller renders the baseline agency-only
        /// decoration.
        ///
        /// <para><b>Why a predicate, not "registry lookup tells me."</b> The
        /// <see cref="AgencySystem.ForeignCrewCount"/> registry is keyed by vessel
        /// id alone — it carries no ownership signal of its own. After
        /// <c>transferagency</c> X→local fires, <see cref="AgencySystem.VesselOwnership"/>
        /// gets the authoritative push (5.18d <c>AgencyVisibilityMsgData</c> via
        /// <see cref="AgencyMembership.ForceRecordOwnership"/>); the
        /// ForeignCrewCount entry stays untouched until the next destructive
        /// reload triggers <c>VesselLoader.ScrubInvalidProtoCrew</c>'s symmetric
        /// eviction branch. Until that reload, the registry holds a stale-but-
        /// harmless count — only this render-time predicate gates whether the
        /// label decoration fires, so the player sees the post-transfer label
        /// immediately (bare <c>[Local Inc.]</c>) rather than the stale
        /// <c>[Crew: 3 (Acme Astronautics)]</c>. Same pattern as
        /// <see cref="AgencyMembership.IsRecoveryBlockedByAgency"/>'s "render-time
        /// re-check is the source of truth" discipline. Phase 6.7 / 6.8 work that
        /// touches <c>VesselOwnership</c> mid-session does NOT need to invalidate
        /// ForeignCrewCount — this predicate makes the lifecycles independent.</para>
        ///
        /// <para><b>Known cosmetic limitation under non-destructive paths.</b>
        /// <c>VesselLoader.UpdateProtoInPlace</c> + <c>UnchangedEarlyOut</c> skip
        /// the scrub-site population/eviction entirely. A foreign vessel whose
        /// crew silently changes between full reloads (e.g. an EVA from a
        /// foreign vessel under PerAgencyKerbalRoster=on — server-canonical, but
        /// the client may take the pointer-swap path) keeps its prior
        /// ForeignCrewCount value until the next destructive reload. The label
        /// surface still renders with the agency tag correctly; only the count
        /// can drift. Spec §7 schedules soak to validate whether this drift is
        /// observable in steady state; if it is, a future tightening can call
        /// <c>ScrubInvalidProtoCrew</c> from the pointer-swap path too.</para>
        /// </summary>
        public static bool IsForeignVessel(
            Guid vesselId,
            Guid localAgencyId,
            ConcurrentDictionary<Guid, Guid> vesselOwnership)
        {
            if (vesselOwnership == null) return false;
            if (localAgencyId == Guid.Empty) return false;
            if (!vesselOwnership.TryGetValue(vesselId, out var owningAgencyId)) return false;
            if (owningAgencyId == Guid.Empty) return false;
            return owningAgencyId != localAgencyId;
        }

        /// <summary>
        /// Stage 6 Phase 6.6 — foreign-agency crew-count label. Formats the
        /// per-vessel decoration the tracking-station / map / flight label surfaces
        /// render for vessels whose kerbal roster was scrubbed locally because the
        /// vessel belongs to a different agency. Returns the inner text of the
        /// bracketed label (caller wraps in <c>[...]</c> per the existing
        /// <see cref="Label.LabelEvents.OnMapWidgetTextProcessed"/> /
        /// <see cref="Label.LabelEvents.OnLabelProcessed"/> /
        /// <see cref="Label.LabelEvents.OnMapLabelProcessed"/> convention) or null
        /// when no enrichment should fire (caller falls back to the bare agency
        /// decoration).
        ///
        /// <para><b>Why we expose count without per-seat detail.</b> Spec §2 Q-Render
        /// signs off "scrub-foreign" as the v1 rendering policy — foreign-agency
        /// kerbal names don't resolve in the local <c>CrewRoster</c>, so
        /// <see cref="VesselUtilities.VesselLoader.ScrubInvalidProtoCrew"/> drops
        /// them before the vessel reaches <see cref="FlightGlobals"/>. The local
        /// <c>vessel.GetCrewCount()</c> reads 0 for fully-foreign vessels post-
        /// scrub, which is misleading — Phase 6.6 reads the snapshot count
        /// captured at scrub time from <see cref="AgencySystem.ForeignCrewCount"/>
        /// and renders the real count alongside the agency tag so peers can
        /// distinguish a foreign crewed mission from a foreign drone.</para>
        ///
        /// <para><b>Branch logic.</b>
        /// <list type="bullet">
        ///   <item><paramref name="crewCount"/> &lt;= 0 → return null. No
        ///         enrichment for vessels with no foreign crew; caller renders the
        ///         baseline <c>[agency]</c> decoration unchanged. Defensive
        ///         negative-input handling guards against a future registry-mutation
        ///         bug seeding a negative value.</item>
        ///   <item><paramref name="agencyDisplayName"/> null/empty → fall back to
        ///         <see cref="UnknownAgencyLabel"/>. Mirrors
        ///         <see cref="FormatVesselAgencyLabel"/>'s empty-name fallback —
        ///         a mid-handshake race window or wire corruption shouldn't
        ///         render an empty-paren string.</item>
        ///   <item>Otherwise → <c>"Crew: {N} ({agencyDisplayName})"</c>. Matches
        ///         spec §2 Q-Render exact text.</item>
        /// </list></para>
        ///
        /// <para><b>Gate.</b> Same as <see cref="FormatVesselAgencyLabel"/>: callers
        /// must check <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled</c>
        /// + <see cref="AgencySystem.LocalAgencyId"/> non-empty + a positive
        /// registry hit before invoking. Helper does NOT re-check the gate.</para>
        /// </summary>
        public static string FormatForeignVesselCrewLabel(int crewCount, string agencyDisplayName)
        {
            if (crewCount <= 0) return null;
            var name = string.IsNullOrEmpty(agencyDisplayName) ? UnknownAgencyLabel : agencyDisplayName;
            return $"Crew: {crewCount} ({name})";
        }
    }
}
