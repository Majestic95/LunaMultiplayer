using LmpClient.Systems.Agency;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LmpClientTest
{
    /// <summary>
    /// Stage 5.18c / per-agency client UI. Pins the pure label-format helper used by
    /// <c>LabelEvents.OnLabelProcessed</c> / <c>OnMapLabelProcessed</c> /
    /// <c>OnMapWidgetTextProcessed</c> when rendering the agency-name prefix on
    /// tracking-station widgets, map nodes, and flight-scene markers.
    ///
    /// The cases below pin every branch of <see cref="AgencySystem.VesselOwnership"/>'s
    /// two-state-sentinel XML — vessel-absent (loading), Empty (Unassigned spec §10
    /// Q3), local-agency, known-other-agency, and unknown-other-agency (late-joining
    /// agency per 5.18a XML). Plus the empty-display-name fallback windows that the
    /// 5.18b handler XML calls out (Handshake → State race window of one Lidgren
    /// message cycle).
    /// </summary>
    [TestClass]
    public class AgencyLabelFormatterTest
    {
        private static ConcurrentDictionary<Guid, Guid> EmptyOwnership() =>
            new ConcurrentDictionary<Guid, Guid>();

        private static Dictionary<Guid, AgencyInfo> EmptyOthers() =>
            new Dictionary<Guid, AgencyInfo>();

        // --- FormatVesselAgencyLabel ---

        [TestMethod]
        public void FormatLabel_VesselAbsent_ReturnsNull()
        {
            // Registry miss: vessel hasn't arrived through a wire round yet (initial
            // sync window) or its proto was relay-stripped of lmpOwningAgency and no
            // authoritative VesselSync has fired. The 5.18b XML prescribes the
            // fallback as "render baseline label unaugmented, never decorate with
            // an Unknown badge during loading."
            var vesselId = Guid.NewGuid();
            var localId = Guid.NewGuid();
            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, localId, "Local Inc.", EmptyOwnership(), EmptyOthers());
            Assert.IsNull(label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsEmpty_ReturnsUnassigned()
        {
            // Spec §10 Q3 Unassigned sentinel: vessel was loaded from a pre-0.31
            // universe (no lmpOwningAgency field) and not yet transferred by
            // Stage 5.18d admin. Any agency may interact; UI shows "Unassigned"
            // so the player understands the slot is up for grabs.
            var vesselId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = Guid.Empty;

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, Guid.NewGuid(), "Local Inc.", ownership, EmptyOthers());
            Assert.AreEqual(AgencyLabelFormatter.UnassignedLabel, label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsLocal_ReturnsLocalDisplayName()
        {
            // Local-agency vessel: render the local display name so the player can
            // visually distinguish their own vessels from peers' at a glance.
            var vesselId = Guid.NewGuid();
            var localId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = localId;

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, localId, "Local Inc.", ownership, EmptyOthers());
            Assert.AreEqual("Local Inc.", label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsLocal_EmptyDisplayName_FallsBackToYourAgency()
        {
            // Mid-Handshake race window: LocalAgencyId is set by AgencyHandshakeMsgData
            // but LocalAgencyDisplayName is set by the immediately-following
            // AgencyStateMsgData. Bounded to one Lidgren tick (5.18a XML) but a
            // window-render landing inside that gap must not render an empty string
            // as the player's agency name — "Your Agency" is the documented fallback.
            var vesselId = Guid.NewGuid();
            var localId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = localId;

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, localId, string.Empty, ownership, EmptyOthers());
            Assert.AreEqual(AgencyLabelFormatter.LocalFallbackLabel, label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsKnownPeer_ReturnsPeerDisplayName()
        {
            var vesselId = Guid.NewGuid();
            var peerId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = peerId;
            var others = EmptyOthers();
            others[peerId] = new AgencyInfo
            {
                AgencyId = peerId,
                OwningPlayerName = "alice",
                DisplayName = "Acme Astronautics"
            };

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, Guid.NewGuid(), "Local Inc.", ownership, others);
            Assert.AreEqual("Acme Astronautics", label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsKnownPeer_EmptyDisplayName_FallsBackToOwningPlayerName()
        {
            // Server populates AgencyInfo.DisplayName from AgencyState.DisplayName,
            // which is always non-empty (RegisterAgency seeds "{player} Space
            // Agency" — never empty). A defensive belt-and-braces case for a
            // future server-side bug or wire corruption: prefer the owning player
            // name over "Unknown" because it carries identifying signal.
            var vesselId = Guid.NewGuid();
            var peerId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = peerId;
            var others = EmptyOthers();
            others[peerId] = new AgencyInfo
            {
                AgencyId = peerId,
                OwningPlayerName = "bob",
                DisplayName = string.Empty
            };

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, Guid.NewGuid(), "Local Inc.", ownership, others);
            Assert.AreEqual("bob", label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsKnownPeer_BothNamesEmpty_FallsBackToUnknown()
        {
            // Pathological case: AgencyInfo present but both name fields empty.
            // Wire corruption / server bug. Render the documented Unknown fallback
            // rather than an empty string (which would look like a missing label).
            var vesselId = Guid.NewGuid();
            var peerId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = peerId;
            var others = EmptyOthers();
            others[peerId] = new AgencyInfo
            {
                AgencyId = peerId,
                OwningPlayerName = string.Empty,
                DisplayName = string.Empty
            };

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, Guid.NewGuid(), "Local Inc.", ownership, others);
            Assert.AreEqual(AgencyLabelFormatter.UnknownAgencyLabel, label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsLateJoiner_ReturnsUnknownAgency()
        {
            // 5.18a XML: OtherAgencies is a one-shot snapshot at handshake time;
            // agencies that come online AFTER are NOT included. Until Stage 5.18d
            // AgencyVisibilityMsgData lands, the only correct render for an agency
            // id absent from OtherAgencies is "Unknown Agency" — never expose the
            // Guid-as-string to the player.
            var vesselId = Guid.NewGuid();
            var lateJoinerId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = lateJoinerId;

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, Guid.NewGuid(), "Local Inc.", ownership, EmptyOthers());
            Assert.AreEqual(AgencyLabelFormatter.UnknownAgencyLabel, label);
        }

        [TestMethod]
        public void FormatLabel_OwningAgencyIsLateJoiner_NullOthersDict_ReturnsUnknownAgency()
        {
            // Defensive: caller could in theory pass null for the OtherAgencies
            // dictionary (e.g. during a not-yet-initialised window draw). Don't
            // NRE on the lookup — fall through to Unknown.
            var vesselId = Guid.NewGuid();
            var lateJoinerId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = lateJoinerId;

            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                vesselId, Guid.NewGuid(), "Local Inc.", ownership, null);
            Assert.AreEqual(AgencyLabelFormatter.UnknownAgencyLabel, label);
        }

        [TestMethod]
        public void FormatLabel_NullRegistry_ReturnsNull()
        {
            // Defensive: AgencySystem.VesselOwnership is non-null by construction
            // but the helper signature accepts it, so a future refactor that lets
            // it go null (e.g. lazy init) won't NRE the LabelEvents hot path.
            var label = AgencyLabelFormatter.FormatVesselAgencyLabel(
                Guid.NewGuid(), Guid.NewGuid(), "Local Inc.", null, EmptyOthers());
            Assert.IsNull(label);
        }

        // --- IsDefaultDisplayName ---

        [TestMethod]
        public void IsDefault_MatchesAutoRegisteredPattern()
        {
            // Server-side AgencySystem.RegisterAgency seeds DisplayName =
            // "{playerName} Space Agency". UI uses this match to render a "Rename?"
            // hint to discoverably surface the rename UX.
            Assert.IsTrue(AgencyLabelFormatter.IsDefaultDisplayName("alice Space Agency", "alice"));
        }

        [TestMethod]
        public void IsDefault_DiffersFromAutoRegistered_ReturnsFalse()
        {
            // Player has already renamed to a custom value; the hint should NOT
            // fire (would be a nag).
            Assert.IsFalse(AgencyLabelFormatter.IsDefaultDisplayName("Acme Astronautics", "alice"));
        }

        [TestMethod]
        public void IsDefault_EmptyDisplayName_ReturnsFalse()
        {
            // Mid-handshake race window (Handshake → State gap). Don't render
            // "Rename?" hint before we even know what the current name is.
            Assert.IsFalse(AgencyLabelFormatter.IsDefaultDisplayName(string.Empty, "alice"));
        }

        [TestMethod]
        public void IsDefault_EmptyOwningPlayerName_ReturnsFalse()
        {
            // Same mid-handshake window from the other side.
            Assert.IsFalse(AgencyLabelFormatter.IsDefaultDisplayName("alice Space Agency", string.Empty));
        }

        [TestMethod]
        public void IsDefault_NullDisplayName_ReturnsFalse()
        {
            Assert.IsFalse(AgencyLabelFormatter.IsDefaultDisplayName(null, "alice"));
        }

        [TestMethod]
        public void IsDefault_NullOwningPlayerName_ReturnsFalse()
        {
            Assert.IsFalse(AgencyLabelFormatter.IsDefaultDisplayName("alice Space Agency", null));
        }

        [TestMethod]
        public void IsDefault_CaseSensitive_MatchesOnlyExact()
        {
            // Player names are case-sensitive on the wire (HandshakeSystem); pin
            // the same case-sensitive comparison here so a player named "Alice"
            // doesn't get the hint when the auto-name is "Alice Space Agency"
            // and they renamed to "alice Space Agency" deliberately.
            Assert.IsFalse(AgencyLabelFormatter.IsDefaultDisplayName("ALICE Space Agency", "alice"));
        }

        // --- IsForeignVessel (Stage 6 Phase 6.6) ---

        [TestMethod]
        public void IsForeign_NullRegistry_False()
        {
            // Defensive: a not-yet-constructed registry must not throw. Same
            // pattern as the null-registry guard on FormatVesselAgencyLabel.
            Assert.IsFalse(AgencyLabelFormatter.IsForeignVessel(
                Guid.NewGuid(), Guid.NewGuid(), null));
        }

        [TestMethod]
        public void IsForeign_LocalAgencyEmpty_False()
        {
            // Mid-handshake window or per-agency gate=off: there is no "local
            // agency" to compare against, so the predicate cannot conclude the
            // vessel is foreign. Belt-and-braces with LabelEvents' own gate check
            // (which already short-circuits before reaching this helper).
            var ownership = EmptyOwnership();
            var vesselId = Guid.NewGuid();
            ownership[vesselId] = Guid.NewGuid();

            Assert.IsFalse(AgencyLabelFormatter.IsForeignVessel(
                vesselId, Guid.Empty, ownership));
        }

        [TestMethod]
        public void IsForeign_VesselAbsent_False()
        {
            // Registry miss: vessel hasn't arrived through a wire round yet.
            // Don't claim it's foreign — the baseline label rendering (which
            // FormatVesselAgencyLabel resolves to null in this case) handles
            // the absent-vessel surface.
            Assert.IsFalse(AgencyLabelFormatter.IsForeignVessel(
                Guid.NewGuid(), Guid.NewGuid(), EmptyOwnership()));
        }

        [TestMethod]
        public void IsForeign_OwningAgencyEmpty_False()
        {
            // Unassigned-sentinel vessel (spec §10 Q3): there is no agency to
            // attribute crew to. The bare "Unassigned" decoration on the row
            // is the right surface; we don't render "Crew: 3 (Unassigned)"
            // because the count without an agency tag is not actionable for
            // the player.
            var vesselId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = Guid.Empty;

            Assert.IsFalse(AgencyLabelFormatter.IsForeignVessel(
                vesselId, Guid.NewGuid(), ownership));
        }

        [TestMethod]
        public void IsForeign_OwningAgencyIsLocal_False()
        {
            // Owner-agency vessel: spec §2 Q-Render says these render unchanged.
            // Local crew count is correct via KSP's own GetCrewCount; the foreign
            // enrichment must not fire.
            var vesselId = Guid.NewGuid();
            var localId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = localId;

            Assert.IsFalse(AgencyLabelFormatter.IsForeignVessel(
                vesselId, localId, ownership));
        }

        [TestMethod]
        public void IsForeign_OwningAgencyIsForeign_True()
        {
            // Happy path: vessel owned by a different agency. Enrichment fires
            // (assuming registry has a positive crew count for it).
            var vesselId = Guid.NewGuid();
            var foreignId = Guid.NewGuid();
            var localId = Guid.NewGuid();
            var ownership = EmptyOwnership();
            ownership[vesselId] = foreignId;

            Assert.IsTrue(AgencyLabelFormatter.IsForeignVessel(
                vesselId, localId, ownership));
        }

        // --- FormatForeignVesselCrewLabel (Stage 6 Phase 6.6) ---

        [TestMethod]
        public void FormatForeignCrew_ZeroCount_ReturnsNull()
        {
            // Foreign vessel arrived with no crew (drone / probe). No enrichment —
            // caller falls back to the bare [agency] suffix already shipped in 5.18c.
            // This is the common case for foreign vessels (most KSP missions are
            // unmanned), so we keep the row label uncluttered.
            Assert.IsNull(AgencyLabelFormatter.FormatForeignVesselCrewLabel(0, "Acme Astronautics"));
        }

        [TestMethod]
        public void FormatForeignCrew_NegativeCount_ReturnsNull()
        {
            // Defensive: a negative registry value would only result from a future
            // mutation bug. Don't render "Crew: -1" — return null so the caller
            // renders the baseline [agency] decoration unchanged. Matches the spec
            // §2 Q-Render's "scrub-foreign" contract: when in doubt, render the
            // identity without false crew claims.
            Assert.IsNull(AgencyLabelFormatter.FormatForeignVesselCrewLabel(-1, "Acme Astronautics"));
        }

        [TestMethod]
        public void FormatForeignCrew_SingleCrew_ReturnsLabel()
        {
            // Singleton-crew foreign vessel (typical solo-pilot mission). Pins the
            // exact spec §2 Q-Render text format — peer must see "Crew: 1 (Acme
            // Astronautics)" so the count + identity render together.
            Assert.AreEqual(
                "Crew: 1 (Acme Astronautics)",
                AgencyLabelFormatter.FormatForeignVesselCrewLabel(1, "Acme Astronautics"));
        }

        [TestMethod]
        public void FormatForeignCrew_MultiCrew_ReturnsLabel()
        {
            // Multi-crew vessel. Same formatting rule. Pinned separately from the
            // singleton case so a future maintainer who specialises the format for
            // count=1 (e.g. "Pilot:" vs "Crew:") doesn't quietly break the multi-
            // crew path.
            Assert.AreEqual(
                "Crew: 4 (Acme Astronautics)",
                AgencyLabelFormatter.FormatForeignVesselCrewLabel(4, "Acme Astronautics"));
        }

        [TestMethod]
        public void FormatForeignCrew_NullAgency_FallsBackToUnknownLabel()
        {
            // Caller arrived with a registry hit (positive count) but the agency
            // identity couldn't be resolved — late-joiner whose AgencyInfo isn't
            // in OtherAgencies, or a defensive null-passthrough. Mirrors
            // FormatVesselAgencyLabel's late-joiner fallback rather than rendering
            // a blank-paren "Crew: N ()" string.
            Assert.AreEqual(
                $"Crew: 2 ({AgencyLabelFormatter.UnknownAgencyLabel})",
                AgencyLabelFormatter.FormatForeignVesselCrewLabel(2, null));
        }

        [TestMethod]
        public void FormatForeignCrew_EmptyAgency_FallsBackToUnknownLabel()
        {
            // Mid-handshake race window where a foreign vessel's proto arrived
            // before its owning agency's AgencyInfo was populated; OtherAgencies
            // lookup succeeded but DisplayName + OwningPlayerName were both empty
            // and FormatVesselAgencyLabel returned UnknownAgencyLabel. The crew
            // formatter sees the same end state — both passes converge on the same
            // fallback so the bracketed surface reads consistently across the two
            // helpers.
            Assert.AreEqual(
                $"Crew: 3 ({AgencyLabelFormatter.UnknownAgencyLabel})",
                AgencyLabelFormatter.FormatForeignVesselCrewLabel(3, string.Empty));
        }
    }
}
