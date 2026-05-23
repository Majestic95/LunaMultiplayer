using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Server;

namespace ServerTest
{
    /// <summary>
    /// Phase 1 of server-side-offload — exercises <see cref="MessageQueuer.ShouldRelayToScene"/>,
    /// the pure helper that drives scene-aware vessel-state relay filtering. Pins every
    /// branch in the decision table so a future "let's filter SpaceCenter too" tweak surfaces
    /// as a deliberate test edit rather than a silent behavior shift.
    ///
    /// Spec: docs/research/11-server-side-offload-spec.md §3.d + §3.h.
    /// </summary>
    [TestClass]
    public class SceneAwareRelayTest
    {
        [TestMethod]
        public void ShouldRelayToScene_Unknown_TrueCompatPath()
        {
            //Pre-Phase-1 client (didn't ship the Scene tail-byte). Server deserialised
            //Unknown. Filter MUST be permissive — relay always — to preserve the
            //pre-Phase-1 broadcast baseline for clients that haven't upgraded.
            Assert.IsTrue(MessageQueuer.ShouldRelayToScene(ClientSceneType.Unknown));
        }

        [TestMethod]
        public void ShouldRelayToScene_Flight_True()
        {
            //Positive path — the dominant case. Player in flight needs every relay.
            Assert.IsTrue(MessageQueuer.ShouldRelayToScene(ClientSceneType.Flight));
        }

        [TestMethod]
        public void ShouldRelayToScene_TrackingStation_True()
        {
            //Tracking station renders remote vessel orbits + maneuvers in real time.
            //Must keep getting relays.
            Assert.IsTrue(MessageQueuer.ShouldRelayToScene(ClientSceneType.TrackingStation));
        }

        [TestMethod]
        public void ShouldRelayToScene_SpaceCenter_False()
        {
            //SpaceCenter (KSC view) does NOT render remote vessels — the in-scene
            //"active vessel marker" is for the LOCAL active vessel on a separate code
            //path. Drop continuous-state relays to this recipient.
            Assert.IsFalse(MessageQueuer.ShouldRelayToScene(ClientSceneType.SpaceCenter));
        }

        [TestMethod]
        public void ShouldRelayToScene_Editor_False()
        {
            //VAB/SPH — building, no remote vessel rendering.
            Assert.IsFalse(MessageQueuer.ShouldRelayToScene(ClientSceneType.Editor));
        }

        [TestMethod]
        public void ShouldRelayToScene_MainMenu_False()
        {
            //Main menu — disconnected from gameplay UI entirely.
            Assert.IsFalse(MessageQueuer.ShouldRelayToScene(ClientSceneType.MainMenu));
        }

        [TestMethod]
        public void ShouldRelayToScene_ResearchAndDevelopment_False()
        {
            //R&D scene placeholder for future mod scenes (no stock KSP R&D scene —
            //it's a UI child of SpaceCenter). If a mod ever reports it, it shouldn't
            //render remote vessels.
            Assert.IsFalse(MessageQueuer.ShouldRelayToScene(ClientSceneType.ResearchAndDevelopment));
        }

        [TestMethod]
        public void ShouldRelayToScene_Mission_False()
        {
            //Making History MissionBuilder — operator-design scene, not a multiplayer
            //rendering context.
            Assert.IsFalse(MessageQueuer.ShouldRelayToScene(ClientSceneType.Mission));
        }

        [TestMethod]
        public void ShouldRelayToScene_Other_False()
        {
            //Catch-all (LOADING / LOADINGBUFFER / PSYSTEM / unrecognised). Player is
            //between scenes — usually sub-second — and wouldn't render the relay.
            Assert.IsFalse(MessageQueuer.ShouldRelayToScene(ClientSceneType.Other));
        }

        [TestMethod]
        public void ShouldRelayToScene_FlightAndTrackingStation_AreTheOnlyTrueNonCompatScenes()
        {
            //Regression-fence: if a future contributor adds a new scene value to the
            //enum AND makes it relay-eligible, this assertion forces them to update
            //this test (which forces them to think about whether the new scene
            //actually renders remote vessel state). The set of "true" scenes under
            //gate=on is intentionally narrow: only Flight + TrackingStation. Unknown
            //is also true but as a compat passthrough, not a "scene that renders" —
            //asserted separately above.
            var trueScenes = 0;
            foreach (ClientSceneType scene in System.Enum.GetValues(typeof(ClientSceneType)))
            {
                if (scene == ClientSceneType.Unknown) continue;  //compat passthrough, not a scene assertion
                if (MessageQueuer.ShouldRelayToScene(scene)) trueScenes++;
            }
            Assert.AreEqual(2, trueScenes,
                "Exactly two non-Unknown scenes (Flight + TrackingStation) should return true. " +
                "Adding a new true scene requires an update to this regression-fence and to the spec.");
        }
    }
}
