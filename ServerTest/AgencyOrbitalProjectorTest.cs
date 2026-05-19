using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Text;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// Phase 3 Slice D-1 — unit tests for <see cref="AgencyScenarioProjector"/>'s
    /// new <c>ScenarioOrbitalLogistics</c> case +
    /// <c>SpliceAgencyOrbitalTransfers</c>. Same shape as the Slice B
    /// <see cref="AgencyKolonyProjectorTest"/> + Slice C
    /// <see cref="AgencyPlanetaryProjectorTest"/>. End-to-end wire coverage
    /// (gate-on cross-agency privacy across two clients) is in
    /// <c>MockClientTest/AgencyOrbitalRoutingTest.cs</c> — deferred to Slice D-2.
    ///
    /// <para><b>Key difference from Slice B/C:</b> orbital uses an
    /// <b>opaque-payload passthrough</b> splice rather than reconstructing the
    /// child node from typed entry fields. Each per-agency
    /// <see cref="AgencyOrbitalTransferEntry.PayloadBytes"/> is the verbatim
    /// UTF-8 ConfigNode-format bytes produced by MKS'
    /// <c>OrbitalLogisticsTransferRequest.Save</c> at state-machine-postfix
    /// time. The projector parses bytes → ConfigNode → adds as TRANSFER
    /// child. We test that the splice round-trips arbitrary opaque content
    /// rather than asserting on specific MKS field names (the actual MKS
    /// schema is the postfix's concern, not the projector's).</para>
    ///
    /// <para><b>Scenario shape</b> (MKS ScenarioOrbitalLogistics.OnSave at
    /// SHA <c>ed0f6aa6</c>): TRANSFER nodes are direct children at the
    /// scenario root — NOT nested under a container like Slice B's
    /// KOLONIZATION or Slice C's PLANETARY_LOGISTICS. The strip pattern
    /// is <c>GetNodes("TRANSFER")</c> at the scenario root directly.</para>
    /// </summary>
    [TestClass]
    public class AgencyOrbitalProjectorTest
    {
        [TestInitialize]
        public void Setup()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();
        }

        [TestCleanup]
        public void Teardown()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            AgencySystem.Reset();
        }

        [TestMethod]
        public void Project_EmptyAgencyOrbital_StripsAllSharedTransfers()
        {
            // Strip-then-splice contract. An agency with zero per-agency
            // transfers projects a scenario with NO TRANSFER children, even
            // if the shared scenario carried transfers from peers — those
            // peers' transfers must not leak into this agency's view (spec
            // §10 Q1 PrivateAgencyResources).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var input = "name = ScenarioOrbitalLogistics\nscene = 7, 8\n" +
                        "TRANSFER\n{\n\tstatus = Launched\n\tStartTime = 100\n\tDuration = 60\n}\n" +
                        "TRANSFER\n{\n\tstatus = Delivered\n\tStartTime = 50\n\tDuration = 10\n}\n";

            var result = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agency);

            Assert.IsFalse(result.Contains("status = Launched"),
                "Shared TRANSFER must be stripped from the projected scenario.");
            Assert.IsFalse(result.Contains("status = Delivered"),
                "Stripped TRANSFER's status must not appear in the projected output.");
        }

        [TestMethod]
        public void Project_AgencyWithTransfers_SplicesOnlyOwn()
        {
            // Per-agency view: the splice emits only the requesting agency's
            // transfers (parsed from PayloadBytes). Shared scenario has 1 peer
            // transfer; agency has 2 own transfers; output has exactly 2
            // transfers (both agency-owned).
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.OrbitalTransfers[Guid.NewGuid()] = TransferEntry(
                AgencyOrbitalTransferEntry.StatusLaunched, "AgencyMarker1");
            agency.OrbitalTransfers[Guid.NewGuid()] = TransferEntry(
                AgencyOrbitalTransferEntry.StatusReturning, "AgencyMarker2");

            var input = "name = ScenarioOrbitalLogistics\n" +
                        "TRANSFER\n{\n\tstatus = Launched\n\tPeerMarker = ThisIsPeerData\n}\n";

            var result = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agency);

            // Peer's TRANSFER stripped:
            Assert.IsFalse(result.Contains("PeerMarker"),
                "Peer's TRANSFER must NOT appear in this agency's projection.");
            Assert.IsFalse(result.Contains("ThisIsPeerData"),
                "Peer's TRANSFER values must NOT leak into this agency's projection.");

            // Own transfers spliced:
            StringAssert.Contains(result, "AgencyMarker1",
                "Agency's first transfer payload must appear in projection.");
            StringAssert.Contains(result, "AgencyMarker2",
                "Agency's second transfer payload must appear in projection.");
        }

        [TestMethod]
        public void Project_OpaquePayloadPassthrough_PreservesArbitraryFields()
        {
            // The orbital splice is opaque-payload passthrough: whatever MKS
            // emitted at OrbitalLogisticsTransferRequest.Save time round-trips
            // unmodified. Unlike Slice B/C splices that reconstruct child nodes
            // from typed entry fields, orbital doesn't know or care about MKS'
            // internal field set. Test by giving the splice a payload with
            // arbitrary fields + nested children and verify they all survive.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var payload = "status = Launched\n" +
                          "StartTime = 12345.6\n" +
                          "Duration = 6789.0\n" +
                          "DestinationVesselId = 12345\n" +
                          "OriginVesselId = 67890\n" +
                          "RESOURCE\n{\n\tResourceDefinition = 374619919\n\tTransferAmount = 100\n}\n";
            agency.OrbitalTransfers[Guid.NewGuid()] = new AgencyOrbitalTransferEntry
            {
                TransferGuid = Guid.NewGuid(),
                OriginVesselId = Guid.NewGuid(),
                DestinationVesselId = Guid.NewGuid(),
                Status = AgencyOrbitalTransferEntry.StatusLaunched,
                StartTime = 12345.6,
                Duration = 6789.0,
                PayloadBytes = Encoding.UTF8.GetBytes(payload),
                NumBytes = Encoding.UTF8.GetByteCount(payload),
            };
            var input = "name = ScenarioOrbitalLogistics\n";

            var result = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agency);

            StringAssert.Contains(result, "status = Launched", "Top-level status field must round-trip.");
            StringAssert.Contains(result, "StartTime = 12345.6", "StartTime must round-trip.");
            StringAssert.Contains(result, "Duration = 6789", "Duration must round-trip.");
            StringAssert.Contains(result, "DestinationVesselId = 12345",
                "DestinationVesselId (MKS' persistentId form) must round-trip — the wire-side opaque-passthrough preserves whatever the client postfix supplied.");
            StringAssert.Contains(result, "RESOURCE", "Nested RESOURCE child must round-trip.");
            StringAssert.Contains(result, "TransferAmount = 100", "Nested RESOURCE values must round-trip.");
        }

        [TestMethod]
        public void Project_NullAgency_ReturnsInputUnchanged()
        {
            var input = "name = ScenarioOrbitalLogistics\nTRANSFER { status = Launched }\n";
            var result = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, null);
            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_NullInput_ReturnsInputUnchanged()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            string input = null;
            var result = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agency);
            Assert.AreEqual(input, result);
        }

        [TestMethod]
        public void Project_EmptyPayloadBytes_EntrySkippedSiblingsRendered()
        {
            // Per-entry isolation: an agency entry with PayloadBytes.Length=0
            // (operator hand-edited / Slice E migration that hasn't yet
            // supplied PayloadBytes) is skipped but siblings continue.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // Empty-payload entry (skipped by splice):
            agency.OrbitalTransfers[Guid.NewGuid()] = new AgencyOrbitalTransferEntry
            {
                TransferGuid = Guid.NewGuid(),
                OriginVesselId = Guid.NewGuid(),
                DestinationVesselId = Guid.NewGuid(),
                Status = AgencyOrbitalTransferEntry.StatusLaunched,
                PayloadBytes = Array.Empty<byte>(),
                NumBytes = 0,
            };
            // Valid sibling (rendered):
            agency.OrbitalTransfers[Guid.NewGuid()] = TransferEntry(
                AgencyOrbitalTransferEntry.StatusLaunched, "ValidSibling");

            var input = "name = ScenarioOrbitalLogistics\n";

            var result = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agency);

            StringAssert.Contains(result, "ValidSibling",
                "Sibling with valid payload must render despite empty-payload sibling being skipped.");
        }

        [TestMethod]
        public void Project_MalformedPayload_PerEntryIsolatedSiblingsRender()
        {
            // Per-entry isolation: an agency entry with malformed PayloadBytes
            // (operator hand-edited a corrupt blob) is dropped by the splice's
            // per-entry try/catch but siblings continue.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // Malformed payload — unbalanced braces would throw inside the
            // ConfigNode parser. Per-entry try/catch isolates.
            agency.OrbitalTransfers[Guid.NewGuid()] = new AgencyOrbitalTransferEntry
            {
                TransferGuid = Guid.NewGuid(),
                OriginVesselId = Guid.NewGuid(),
                DestinationVesselId = Guid.NewGuid(),
                Status = AgencyOrbitalTransferEntry.StatusLaunched,
                PayloadBytes = Encoding.UTF8.GetBytes("status = Launched\n{ { { unbalanced"),
                NumBytes = "status = Launched\n{ { { unbalanced".Length,
            };
            // Valid sibling:
            agency.OrbitalTransfers[Guid.NewGuid()] = TransferEntry(
                AgencyOrbitalTransferEntry.StatusLaunched, "AdjacentValid");
            var input = "name = ScenarioOrbitalLogistics\n";

            var result = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agency);

            StringAssert.Contains(result, "AdjacentValid",
                "Sibling with valid payload must render despite malformed-payload sibling being dropped.");
        }

        [TestMethod]
        public void Project_NoCrossAgencyTransferLeak_ProjectionIsAgencyScoped()
        {
            // Spec §10 Q1 PrivateAgencyResources. Two agencies each get their
            // own projection — the splice reads from the *target* agency's
            // OrbitalTransfers only, never the other's.
            var agencyA = new AgencyState { AgencyId = Guid.NewGuid() };
            var agencyB = new AgencyState { AgencyId = Guid.NewGuid() };
            agencyA.OrbitalTransfers[Guid.NewGuid()] = TransferEntry(
                AgencyOrbitalTransferEntry.StatusLaunched, "AlicePayload");
            agencyB.OrbitalTransfers[Guid.NewGuid()] = TransferEntry(
                AgencyOrbitalTransferEntry.StatusReturning, "BobPayload");
            var input = "name = ScenarioOrbitalLogistics\n";

            var resultA = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agencyA);
            var resultB = AgencyScenarioProjector.Project("ScenarioOrbitalLogistics", input, agencyB);

            StringAssert.Contains(resultA, "AlicePayload",
                "Agency A's projection must contain own transfer payload.");
            Assert.IsFalse(resultA.Contains("BobPayload"),
                "Agency A's projection must NOT contain agency B's transfer payload.");
            StringAssert.Contains(resultB, "BobPayload",
                "Agency B's projection must contain own transfer payload.");
            Assert.IsFalse(resultB.Contains("AlicePayload"),
                "Agency B's projection must NOT contain agency A's transfer payload.");
        }

        [TestMethod]
        public void Project_NonOrbitalScenarioName_ReturnsInputUnchanged()
        {
            // Defensive: the projector's switch routes unknown scenario names
            // to the default branch which returns input unchanged. A caller
            // passing the wrong name doesn't accidentally trigger the orbital
            // splice on (say) a planetary scenario blob.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            agency.OrbitalTransfers[Guid.NewGuid()] = TransferEntry(
                AgencyOrbitalTransferEntry.StatusLaunched, "WouldOnlyAppearIfWrongRouting");

            // Use a clearly non-orbital name; the projector should not splice.
            var input = "name = SomeOtherScenario\nTRANSFER { status = Launched }\n";
            var result = AgencyScenarioProjector.Project("SomeOtherScenario", input, agency);

            Assert.AreEqual(input, result);
            Assert.IsFalse(result.Contains("WouldOnlyAppearIfWrongRouting"),
                "Non-orbital scenario name must not trigger orbital splice.");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds an <see cref="AgencyOrbitalTransferEntry"/> whose PayloadBytes
        /// is the UTF-8 representation of a minimal TRANSFER ConfigNode body
        /// carrying a unique <paramref name="markerToken"/>. The marker lets
        /// tests assert "this agency's transfer appeared in projection" by
        /// substring without needing to mirror MKS' full field set.
        /// </summary>
        private static AgencyOrbitalTransferEntry TransferEntry(int status, string markerToken)
        {
            var payload = $"status = {StatusName(status)}\nMarkerToken = {markerToken}\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            return new AgencyOrbitalTransferEntry
            {
                TransferGuid = Guid.NewGuid(),
                OriginVesselId = Guid.NewGuid(),
                DestinationVesselId = Guid.NewGuid(),
                Status = status,
                StartTime = 0,
                Duration = 0,
                PayloadBytes = bytes,
                NumBytes = bytes.Length,
            };
        }

        private static string StatusName(int status)
        {
            switch (status)
            {
                case AgencyOrbitalTransferEntry.StatusPreLaunch: return "PreLaunch";
                case AgencyOrbitalTransferEntry.StatusLaunched: return "Launched";
                case AgencyOrbitalTransferEntry.StatusCancelled: return "Cancelled";
                case AgencyOrbitalTransferEntry.StatusPartial: return "Partial";
                case AgencyOrbitalTransferEntry.StatusDelivered: return "Delivered";
                case AgencyOrbitalTransferEntry.StatusFailed: return "Failed";
                case AgencyOrbitalTransferEntry.StatusReturning: return "Returning";
                default: return status.ToString();
            }
        }
    }
}
