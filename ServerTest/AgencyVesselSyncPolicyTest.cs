using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.18d slice (i) <see cref="AgencyVesselSyncPolicy"/>
    /// force-full-sync-on-reconnect decision. Both inputs (gate state + per-
    /// connection has-synced-once flag) interact; pin every combination.
    ///
    /// <para>The bypass-only behaviour is load-bearing: under gate=off the
    /// legacy incremental diff is correct AND saves bandwidth on reconnect; we
    /// don't want to force full-sync there. Under gate=on, only the FIRST sync
    /// per connection should be full — subsequent syncs (during the same
    /// connection) take the diff path because the registry is already
    /// populated.</para>
    /// </summary>
    [TestClass]
    public class AgencyVesselSyncPolicyTest
    {
        [TestMethod]
        public void ShouldFullSync_GateOn_FirstSync_ReturnsTrue()
        {
            // Reconnect-into-per-agency-mode case. Client has reconnected;
            // ClientStructure.HasReceivedInitialVesselsSync is the default
            // false (set false at struct construction); the gate is on.
            // Force the full sync.
            Assert.IsTrue(AgencyVesselSyncPolicy.ShouldFullSync(
                perAgencyGateOn: true,
                hasReceivedInitialVesselsSync: false));
        }

        [TestMethod]
        public void ShouldFullSync_GateOn_SubsequentSync_ReturnsFalse()
        {
            // The client already received its initial sync this connection;
            // any subsequent VesselSyncMsgData should take the legacy diff
            // (incremental updates only). The first-sync flag is per-
            // connection, so reconnect resets it.
            Assert.IsFalse(AgencyVesselSyncPolicy.ShouldFullSync(
                perAgencyGateOn: true,
                hasReceivedInitialVesselsSync: true));
        }

        [TestMethod]
        public void ShouldFullSync_GateOff_FirstSync_ReturnsFalse()
        {
            // Dual-mode silence (spec §11): under gate=off there is no per-
            // agency stamp to propagate; the legacy diff is correct and
            // saves bandwidth on reconnect. Don't force full sync.
            Assert.IsFalse(AgencyVesselSyncPolicy.ShouldFullSync(
                perAgencyGateOn: false,
                hasReceivedInitialVesselsSync: false));
        }

        [TestMethod]
        public void ShouldFullSync_GateOff_SubsequentSync_ReturnsFalse()
        {
            Assert.IsFalse(AgencyVesselSyncPolicy.ShouldFullSync(
                perAgencyGateOn: false,
                hasReceivedInitialVesselsSync: true));
        }

        [TestMethod]
        public void ShouldFullSync_OnlyFiresOnGateOnAndFirstSyncBoth()
        {
            // Truth table of the AND condition. Only one of four cases
            // returns true. Pinning the whole 2x2 grid in one test guards
            // against an accidental rewrite that flips the boolean
            // operator.
            Assert.IsTrue(AgencyVesselSyncPolicy.ShouldFullSync(true, false));
            Assert.IsFalse(AgencyVesselSyncPolicy.ShouldFullSync(true, true));
            Assert.IsFalse(AgencyVesselSyncPolicy.ShouldFullSync(false, false));
            Assert.IsFalse(AgencyVesselSyncPolicy.ShouldFullSync(false, true));
        }
    }
}
