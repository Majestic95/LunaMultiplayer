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
    }
}
