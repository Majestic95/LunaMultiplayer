using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.System;
using System.Collections.Generic;
using System.Linq;

namespace ServerTest
{
    [TestClass]
    public class WarpSoloDetectionTest
    {
        private static Subspace MakeSubspace(int id, bool solo = false)
        {
            var s = new Subspace(id, 0d, "test") { Solo = solo };
            return s;
        }

        [TestMethod]
        public void DetectSoloTransitions_EmptyInputs_YieldsNoTransitions()
        {
            var transitions = WarpSystem.DetectSoloTransitions(new Subspace[0], new int[0]).ToArray();
            Assert.AreEqual(0, transitions.Length);
        }

        [TestMethod]
        public void DetectSoloTransitions_SingleClient_FlipsSubspaceToSolo()
        {
            var subspaces = new[] { MakeSubspace(7, solo: false) };
            var clientSubspaceIds = new[] { 7 };

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(1, transitions.Length);
            Assert.AreEqual(7, transitions[0].Subspace.Id);
            Assert.IsTrue(transitions[0].NewSolo);
        }

        [TestMethod]
        public void DetectSoloTransitions_SingleClientAlreadySolo_NoTransition()
        {
            var subspaces = new[] { MakeSubspace(7, solo: true) };
            var clientSubspaceIds = new[] { 7 };

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(0, transitions.Length);
        }

        [TestMethod]
        public void DetectSoloTransitions_TwoClients_FlipsSubspaceToNonSolo()
        {
            var subspaces = new[] { MakeSubspace(7, solo: true) };
            var clientSubspaceIds = new[] { 7, 7 };

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(1, transitions.Length);
            Assert.AreEqual(7, transitions[0].Subspace.Id);
            Assert.IsFalse(transitions[0].NewSolo);
        }

        [TestMethod]
        public void DetectSoloTransitions_EmptySubspaceWasSolo_FlipsToNonSolo()
        {
            //Edge case: a player who was alone in subspace 7 disconnects. The subspace itself
            //will be reaped by RemoveSubspace if non-latest, but until then it appears in
            //WarpContext.Subspaces with 0 occupants. Solo flag must reset to false.
            var subspaces = new[] { MakeSubspace(7, solo: true) };
            var clientSubspaceIds = new int[0];

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(1, transitions.Length);
            Assert.IsFalse(transitions[0].NewSolo);
        }

        [TestMethod]
        public void DetectSoloTransitions_EmptySubspaceNotSolo_NoTransition()
        {
            var subspaces = new[] { MakeSubspace(7, solo: false) };
            var clientSubspaceIds = new int[0];

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(0, transitions.Length);
        }

        [TestMethod]
        public void DetectSoloTransitions_MultipleSubspaces_EachEvaluatedIndependently()
        {
            var subspaces = new[]
            {
                MakeSubspace(1, solo: false),  // 1 client  -> flip to solo
                MakeSubspace(2, solo: true),   // 2 clients -> flip to non-solo
                MakeSubspace(3, solo: false),  // 3 clients -> no change (still non-solo)
                MakeSubspace(4, solo: true),   // 1 client  -> no change (still solo)
                MakeSubspace(5, solo: false)   // 0 clients -> no change
            };
            var clientSubspaceIds = new[] { 1, 2, 2, 3, 3, 3, 4 };

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(2, transitions.Length);

            var byId = transitions.ToDictionary(t => t.Subspace.Id);
            Assert.IsTrue(byId.ContainsKey(1));
            Assert.IsTrue(byId[1].NewSolo);
            Assert.IsTrue(byId.ContainsKey(2));
            Assert.IsFalse(byId[2].NewSolo);
        }

        [TestMethod]
        public void DetectSoloTransitions_NullSubspace_IsSkipped()
        {
            var subspaces = new[] { null, MakeSubspace(7, solo: false) };
            var clientSubspaceIds = new[] { 7 };

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(1, transitions.Length);
            Assert.AreEqual(7, transitions[0].Subspace.Id);
            Assert.IsTrue(transitions[0].NewSolo);
        }

        [TestMethod]
        public void DetectSoloTransitions_ClientInUnknownSubspace_DoesNotAffectKnownOnes()
        {
            //A client reporting a subspace ID we don't have (e.g. -1 while warping, or a deleted
            //subspace) must not count toward any known subspace's occupant total.
            var subspaces = new[] { MakeSubspace(7, solo: false) };
            var clientSubspaceIds = new[] { 7, -1, 999 };

            var transitions = WarpSystem.DetectSoloTransitions(subspaces, clientSubspaceIds).ToArray();

            Assert.AreEqual(1, transitions.Length);
            Assert.AreEqual(7, transitions[0].Subspace.Id);
            Assert.IsTrue(transitions[0].NewSolo);  // exactly one occupant (the client in 7)
        }
    }
}
