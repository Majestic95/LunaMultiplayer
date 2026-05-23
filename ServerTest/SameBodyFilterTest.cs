using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Server;

namespace ServerTest
{
    /// <summary>
    /// Phase 2 of server-side-offload — exercises <see cref="MessageQueuer.ShouldRelayToBody"/>,
    /// the pure helper that drives same-body vessel-state relay filtering. Pins every
    /// branch so a future "let's add SOI-graph awareness" tweak surfaces as a
    /// deliberate test edit rather than a silent behavior shift.
    ///
    /// Spec: docs/research/11-server-side-offload-spec.md §4.
    /// </summary>
    [TestClass]
    public class SameBodyFilterTest
    {
        [TestMethod]
        public void ShouldRelayToBody_SenderBodyNull_TruePermissive()
        {
            //Sender's body unknown (e.g. Phase 2 lookup fell through for a non-Position
            //message and VesselStoreSystem doesn't have the vessel yet). Filter MUST
            //be permissive to avoid silently dropping legitimate state during the
            //first-ingest window.
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody(null, "Kerbin"));
        }

        [TestMethod]
        public void ShouldRelayToBody_SenderBodyEmpty_TruePermissive()
        {
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody(string.Empty, "Kerbin"));
        }

        [TestMethod]
        public void ShouldRelayToBody_RecipientBodyNull_TruePermissive()
        {
            //Recipient hasn't sent first Flightstate-followed-by-Position-for-active yet.
            //Permissive for the join window — recipient gets at least one tick of state
            //before the body filter starts applying.
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody("Kerbin", null));
        }

        [TestMethod]
        public void ShouldRelayToBody_RecipientBodyEmpty_TruePermissive()
        {
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody("Kerbin", string.Empty));
        }

        [TestMethod]
        public void ShouldRelayToBody_SameBody_True()
        {
            //Positive path — Alice at Kerbin, Bob at Kerbin → relay.
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody("Kerbin", "Kerbin"));
        }

        [TestMethod]
        public void ShouldRelayToBody_DifferentBodies_False()
        {
            //The whole point of Phase 2 — Alice at Jool, Bob at Kerbin → drop.
            Assert.IsFalse(MessageQueuer.ShouldRelayToBody("Jool", "Kerbin"));
        }

        [TestMethod]
        public void ShouldRelayToBody_MoonVsParentBody_False_ConservativeSameBodyOnly()
        {
            //Regression-fence on the §4.d decision: same-body-only, NOT SOI-aware.
            //Mun is in Kerbin's SOI, but the filter drops anyway because we don't
            //ship a SOI graph (modded planet packs would break it). If someone
            //adds SOI-aware filtering later, this test forces them to update the
            //decision-table doc.
            Assert.IsFalse(MessageQueuer.ShouldRelayToBody("Mun", "Kerbin"));
            Assert.IsFalse(MessageQueuer.ShouldRelayToBody("Minmus", "Kerbin"));
        }

        [TestMethod]
        public void ShouldRelayToBody_CaseSensitive_TreatsAsDifferent()
        {
            //StringComparison.Ordinal — KSP body names are case-deterministic ("Kerbin"
            //not "kerbin"). If a wire field arrives with a different case, that's
            //either a bug in the sending client or a modded body name we shouldn't
            //silently coalesce. Explicit pin.
            Assert.IsFalse(MessageQueuer.ShouldRelayToBody("Kerbin", "kerbin"));
        }

        [TestMethod]
        public void ShouldRelayToBody_PlanetPackBodies_True()
        {
            //RSS / OPM / GPP body names. The filter doesn't care what the body is
            //called as long as both sides report the same string. Pin a couple of
            //common modded planet-pack body names to make sure StringComparison
            //handles them.
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody("Sarnus", "Sarnus"));
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody("Earth", "Earth"));
        }

        [TestMethod]
        public void ShouldRelayToBody_NullVsEmpty_BothPermissive()
        {
            //null and empty must behave identically for both arguments. string.IsNullOrEmpty
            //handles both; this pin makes sure a future refactor to explicit null-check
            //doesn't regress the empty-string branch.
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody(null, null));
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody(string.Empty, string.Empty));
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody(null, string.Empty));
            Assert.IsTrue(MessageQueuer.ShouldRelayToBody(string.Empty, null));
        }

        // ─────────────────────────────────────────────────────────────────
        // ResolveSenderBody — Phase 2 M1 fix exercises the post-race cache
        // path that read off Vessel.CurrentBodyName instead of the
        // Orbit MixedCollection traversal.
        // ─────────────────────────────────────────────────────────────────

        [TestMethod]
        public void ResolveSenderBody_PositionMsgData_ReadsBodyNameFromMessage()
        {
            //Position carries body inline — fastest path, no store lookup, no race.
            var posMsg = new VesselPositionMsgDataAccessor(System.Guid.NewGuid(), "Eve");
            var body = InvokeResolveSenderBody(posMsg.Inner);
            Assert.AreEqual("Eve", body);
        }

        [TestMethod]
        public void ResolveSenderBody_PositionMsgDataEmptyBody_ReturnsEmpty()
        {
            //Position deserialised before BodyName tail-field landed (pre-fork msg or
            //wire-format edge). Returns the empty value; the filter's permissive null/
            //empty handling at ShouldRelayToBody covers downstream.
            var posMsg = new VesselPositionMsgDataAccessor(System.Guid.NewGuid(), string.Empty);
            var body = InvokeResolveSenderBody(posMsg.Inner);
            Assert.AreEqual(string.Empty, body);
        }

        //(Defensive non-VesselBaseMsgData branch is exercised indirectly — the composed
        //RelayMessageToFlightSceneSameBody is only ever called from VesselMsgReader
        //sites in production, so an IMessageData that isn't a vessel message can't
        //reach ResolveSenderBody. The fallback `return null;` is a future-maintainer
        //guard; pinning it would require synthesising a non-vessel IMessageData with
        //factory-internal ctors which adds reflection complexity without protecting
        //a real call path.)

        // Reflection invocation — ResolveSenderBody is `internal static` (only the
        // composed filter needs it). The dependency on VesselStoreSystem requires
        // a populated store to test the non-Position branch end-to-end; pinning
        // that exhaustively belongs in an integration test (MockClientTest e2e
        // queued for the workstream's soak window). The 3 cases above pin the
        // load-bearing branches that don't need the store.
        private static string InvokeResolveSenderBody(LmpCommon.Message.Interface.IMessageData data)
        {
            var method = typeof(MessageQueuer).GetMethod("ResolveSenderBody",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "ResolveSenderBody helper not found via reflection");
            return (string)method.Invoke(null, new object[] { data });
        }

        // Small accessor — VesselPositionMsgData's ctor is internal so direct
        // construction in tests requires reflection. Wrapping it once keeps the
        // tests above readable.
        private class VesselPositionMsgDataAccessor
        {
            public LmpCommon.Message.Data.Vessel.VesselPositionMsgData Inner { get; }
            public VesselPositionMsgDataAccessor(System.Guid vesselId, string bodyName)
            {
                Inner = (LmpCommon.Message.Data.Vessel.VesselPositionMsgData)
                    typeof(LmpCommon.Message.Data.Vessel.VesselPositionMsgData)
                    .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, System.Type.EmptyTypes, null)
                    .Invoke(null);
                Inner.VesselId = vesselId;
                Inner.BodyName = bodyName;
            }
        }
    }
}
