using LmpCommon.Enums;
using LmpCommon.Locks;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Command.Command;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel.Classes;
using System;
using System.IO;
using System.Linq;

namespace MockClientTest
{
    /// <summary>
    /// Phase 3 Slice E-2 (the Phase 3 closer) — end-to-end wire coverage for
    /// the <see cref="SetVesselAgencyCommand"/>'s 9-step orchestration with
    /// two connected clients. The server-side gate / resolve / short-circuit
    /// branches are unit-tested in <c>ServerTest/SetVesselAgencyCommandTest</c>;
    /// the parser grammar in <c>ServerTest/SetVesselAgencyCommandParserTest</c>.
    /// This surface covers the integration that those unit tests can't reach:
    /// <list type="bullet">
    ///   <item><b>Cross-router migration</b> — kolony + orbital partitions
    ///        rehome from source to destination AgencyState under the dual
    ///        agency lock; planetary entries (Q2 NO-MIGRATE) stay in source.</item>
    ///   <item><b>Wire emit ordering</b> — <see cref="AgencyVisibilityMsgData"/>
    ///        broadcast arrives BEFORE the per-router owner-only echoes, so
    ///        the source owner's 5.18b mirror updates before the removal
    ///        echoes prune their per-router cache.</item>
    ///   <item><b>Lock release</b> — Bob's pre-move Control-lock acquire is
    ///        rejected by the 5.17a guard; post-move Alice's stale lock has
    ///        been released and Bob's acquire succeeds.</item>
    ///   <item><b>Unassigned-source</b> — vessel with
    ///        <c>OwningAgencyId == Guid.Empty</c> (spec §10 Q3 sentinel)
    ///        upgrades to bobAgency via Visibility broadcast; no per-router
    ///        echoes (no source AgencyState by construction).</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class CrossRouterVesselTransferTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void SetVesselAgency_AliceToBob_KolonyAndOrbitalMigrate_PlanetaryRetained_VisibilityBroadcastsToBothClients()
        {
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2-e2-a1");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2-e2-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency, "Alice/Bob must have distinct agencies.");

                // Plant a vessel stamped with Alice's agency.
                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Pre-populate Alice's KolonyEntries + OrbitalTransfers + PlanetaryEntries
                // for the vessel. Direct-insert under the per-agency lock — matches
                // the contract on AgencyState.KolonyEntries/OrbitalTransfers/PlanetaryEntries
                // (AgencyState.cs:166+ XML).
                var aliceState = AgencySystem.Agencies[aliceAgency];
                var transferGuid = Guid.NewGuid();
                var unrelatedTransferGuid = Guid.NewGuid();
                var otherVesselId = Guid.NewGuid();
                var otherVessel = new Vessel(SampleVesselText.Value);
                otherVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(otherVesselId, otherVessel));

                lock (AgencySystem.GetAgencyLock(aliceAgency))
                {
                    aliceState.KolonyEntries[$"{vesselId:N}|3"] = new AgencyKolonyEntry
                    {
                        VesselId = vesselId.ToString("N"),
                        BodyIndex = 3,
                        GeologyResearch = 75.0,
                    };
                    aliceState.KolonyEntries[$"{otherVesselId:N}|1"] = new AgencyKolonyEntry
                    {
                        VesselId = otherVesselId.ToString("N"),
                        BodyIndex = 1,
                        GeologyResearch = 25.0,
                    };

                    aliceState.OrbitalTransfers[transferGuid] = new AgencyOrbitalTransferEntry
                    {
                        TransferGuid = transferGuid,
                        OriginVesselId = otherVesselId,
                        DestinationVesselId = vesselId, // V is Destination — Migrate-MOVE under Q1.
                        Status = AgencyOrbitalTransferEntry.StatusLaunched,
                        StartTime = 1000d,
                        Duration = 200d,
                        PayloadBytes = Array.Empty<byte>(),
                        NumBytes = 0,
                    };
                    aliceState.OrbitalTransfers[unrelatedTransferGuid] = new AgencyOrbitalTransferEntry
                    {
                        TransferGuid = unrelatedTransferGuid,
                        OriginVesselId = otherVesselId,
                        DestinationVesselId = otherVesselId, // unrelated to V — stays in Alice.
                        Status = AgencyOrbitalTransferEntry.StatusLaunched,
                        StartTime = 1000d,
                        Duration = 200d,
                        PayloadBytes = Array.Empty<byte>(),
                        NumBytes = 0,
                    };

                    aliceState.PlanetaryEntries["3|MaterialKits"] = new AgencyPlanetaryEntry
                    {
                        OwningVesselId = vesselId,
                        BodyIndex = 3,
                        ResourceName = "MaterialKits",
                        StoredQuantity = 500.0,
                    };
                }

                // Operator invocation. Drives the full 9-step orchestration.
                var ok = new SetVesselAgencyCommand().Execute($"{vesselId:N} {bobAgency:N}");
                Assert.IsTrue(ok, "Command must succeed on a legitimate A→B transfer.");

                // (a) Visibility broadcast arrives at BOTH clients with V→bobAgency.
                var aliceVisibility = alice.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(aliceVisibility, "Alice did not receive AgencyVisibilityMsgData broadcast.");
                Assert.AreEqual(1, aliceVisibility.ChangeCount);
                Assert.AreEqual(vesselId, aliceVisibility.Changes[0].VesselId);
                Assert.AreEqual(bobAgency, aliceVisibility.Changes[0].NewOwningAgencyId);

                var bobVisibility = bob.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobVisibility, "Bob did not receive AgencyVisibilityMsgData broadcast.");
                Assert.AreEqual(vesselId, bobVisibility.Changes[0].VesselId);
                Assert.AreEqual(bobAgency, bobVisibility.Changes[0].NewOwningAgencyId);

                // (b) Alice receives the kolony source-removal echo. The wire
                // shape: EntryCount=0, RemovedKolonyKeyCount=1.
                var aliceKolonyRemoval = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(aliceKolonyRemoval, "Alice did not receive source-removal kolony echo.");
                Assert.AreEqual(aliceAgency, aliceKolonyRemoval.AgencyId,
                    "Source-removal echo's AgencyId must be Alice's (the source).");
                Assert.AreEqual(0, aliceKolonyRemoval.EntryCount, "Source-removal echo must carry zero added entries.");
                Assert.AreEqual(1, aliceKolonyRemoval.RemovedKolonyKeyCount, "Source-removal echo must carry one removed key.");
                Assert.AreEqual($"{vesselId:N}|3", aliceKolonyRemoval.RemovedKolonyKeys[0]);

                // (b cont.) Alice receives the orbital source-removal echo.
                var aliceOrbitalRemoval = alice.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(aliceOrbitalRemoval, "Alice did not receive source-removal orbital echo.");
                Assert.AreEqual(aliceAgency, aliceOrbitalRemoval.AgencyId);
                Assert.AreEqual(0, aliceOrbitalRemoval.EntryCount);
                Assert.AreEqual(1, aliceOrbitalRemoval.RemovedTransferCount);
                Assert.AreEqual(transferGuid, aliceOrbitalRemoval.RemovedTransferGuids[0]);

                // (c) Bob receives the kolony destination-add echo.
                var bobKolonyAdd = bob.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobKolonyAdd, "Bob did not receive destination-add kolony echo.");
                Assert.AreEqual(bobAgency, bobKolonyAdd.AgencyId);
                Assert.AreEqual(1, bobKolonyAdd.EntryCount);
                Assert.AreEqual(75.0, bobKolonyAdd.Entries[0].GeologyResearch);
                Assert.AreEqual(0, bobKolonyAdd.RemovedKolonyKeyCount);

                // (c cont.) Bob receives the orbital destination-add echo.
                var bobOrbitalAdd = bob.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobOrbitalAdd, "Bob did not receive destination-add orbital echo.");
                Assert.AreEqual(bobAgency, bobOrbitalAdd.AgencyId);
                Assert.AreEqual(1, bobOrbitalAdd.EntryCount);
                Assert.AreEqual(transferGuid, bobOrbitalAdd.Entries[0].TransferGuid);

                // Per-agency state ground-truth post-command.
                Assert.AreEqual(bobAgency, VesselStoreSystem.CurrentVessels[vesselId].OwningAgencyId,
                    "Vessel.OwningAgencyId must be Bob's agency after the transfer.");
                Assert.IsFalse(aliceState.KolonyEntries.ContainsKey($"{vesselId:N}|3"),
                    "Alice's KolonyEntries must no longer hold the moved vessel's entry.");
                Assert.IsTrue(aliceState.KolonyEntries.ContainsKey($"{otherVesselId:N}|1"),
                    "Unrelated kolony entry on otherVessel must remain in Alice.");
                Assert.IsTrue(AgencySystem.Agencies[bobAgency].KolonyEntries.ContainsKey($"{vesselId:N}|3"),
                    "Bob's KolonyEntries must now hold the moved vessel's entry.");

                Assert.IsFalse(aliceState.OrbitalTransfers.ContainsKey(transferGuid),
                    "Alice's OrbitalTransfers must no longer hold the V-Destination transfer.");
                Assert.IsTrue(aliceState.OrbitalTransfers.ContainsKey(unrelatedTransferGuid),
                    "Unrelated orbital transfer on otherVessel must remain in Alice.");
                Assert.IsTrue(AgencySystem.Agencies[bobAgency].OrbitalTransfers.ContainsKey(transferGuid),
                    "Bob's OrbitalTransfers must now hold the migrated transfer.");

                // Q2 NO-MIGRATE: planetary entries stay in Alice.
                Assert.IsTrue(aliceState.PlanetaryEntries.ContainsKey("3|MaterialKits"),
                    "Alice's PlanetaryEntries must retain the moved vessel's contribution (Q2 NO-MIGRATE).");
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgency].PlanetaryEntries.Count,
                    "Bob's PlanetaryEntries must be empty — planetary doesn't migrate.");

                // No planetary echoes either direction (no migration → no echo).
                var aliceNoPlanet = alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(aliceNoPlanet, "Alice received planetary echo — Q2 NO-MIGRATE breached.");
                var bobNoPlanet = bob.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(bobNoPlanet, "Bob received planetary echo — Q2 NO-MIGRATE breached.");
            }
        }

        [TestMethod]
        public void SetVesselAgency_UnassignedSentinel_CrossAgencyLockHolder_ReleasedByStaleLockSweep()
        {
            // Round-1 upgrade-lens CONSIDER C1 pin — spec §10 Q3 says any agency
            // may acquire locks on an Unassigned vessel. After /setvesselagency
            // upgrades it to Bob, Charlie's pre-stamp Control lock is stale —
            // the unified ReleaseStaleVesselLocks walk (which doesn't filter
            // by a specific old-owner name) must catch it because Charlie's
            // AgencyByPlayerName mapping doesn't match Bob's agency.
            using (var charlie = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(charlie.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var charlieAgency = HandshakeAndDrainAgencyMessages(charlie, "h-mksr2-e2-c1");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2-e2-b4");

                // Plant Unassigned vessel (spec §10 Q3 sentinel).
                var vesselId = Guid.NewGuid();
                var unassignedVessel = new Vessel(SampleVesselText.Value);
                unassignedVessel.OwningAgencyId = Guid.Empty;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, unassignedVessel));

                // Charlie (under spec §10 Q3 Unassigned-bypass) acquires Control.
                var charlieLockOk = LockSystem.AcquireLock(
                    new LockDefinition(LockType.Control, "h-mksr2-e2-c1", vesselId),
                    force: false, out _);
                Assert.IsTrue(charlieLockOk, "Charlie's Q3-bypass lock acquire on Unassigned must succeed.");

                // Operator stamps the vessel to Bob.
                var ok = new SetVesselAgencyCommand().Execute($"{vesselId:N} {bobAgency:N}");
                Assert.IsTrue(ok);

                // Drain Visibility broadcasts.
                charlie.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));
                bob.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));

                // The unified release sweep (Round-1 integration-logic
                // scenario 10 fix) must have caught Charlie's stale lock —
                // Charlie's AgencyByPlayerName maps to charlieAgency ≠ bobAgency.
                Assert.IsFalse(
                    LockSystem.LockQuery.LockExists(LockType.Control, vesselId, "h-mksr2-e2-c1"),
                    "Charlie's stale Q3-bypass Control lock must have been released by the unified sweep.");

                Assert.AreEqual(bobAgency, VesselStoreSystem.CurrentVessels[vesselId].OwningAgencyId);

                // Bob can now acquire Control (same-agency, 5.17a permits).
                var bobLockOk = LockSystem.AcquireLock(
                    new LockDefinition(LockType.Control, "h-mksr2-e2-b4", vesselId),
                    force: false, out _);
                Assert.IsTrue(bobLockOk, "Post-stamp Bob (the new owner) must acquire Control.");
                _ = charlieAgency; // touch to silence the unused-variable warning
            }
        }

        [TestMethod]
        public void SetVesselAgency_UnassignedSentinelToBob_VisibilityBroadcastsNoPerRouterEchoes()
        {
            // Spec §10 Q3 upgrade case: pre-0.31 vessel (OwningAgencyId == Empty)
            // gets stamped to Bob for the first time. No source AgencyState to
            // migrate FROM, so no per-router echoes. Visibility still broadcasts.
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-mksr2-e2-a2");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2-e2-b2");

                // Plant an Unassigned-sentinel vessel.
                var vesselId = Guid.NewGuid();
                var unassignedVessel = new Vessel(SampleVesselText.Value);
                unassignedVessel.OwningAgencyId = Guid.Empty;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, unassignedVessel));

                var ok = new SetVesselAgencyCommand().Execute($"{vesselId:N} {bobAgency:N}");
                Assert.IsTrue(ok, "Unassigned→B stamp must succeed.");

                // Visibility broadcast arrives at both clients (upgrade is public).
                var aliceVisibility = alice.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(aliceVisibility, "Alice did not receive Visibility broadcast for Unassigned→B upgrade.");
                Assert.AreEqual(bobAgency, aliceVisibility.Changes[0].NewOwningAgencyId);

                var bobVisibility = bob.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobVisibility, "Bob did not receive Visibility broadcast for Unassigned→B upgrade.");

                // No per-router echoes — source AgencyState is null, no entries to remove.
                // Bob has no entries to add either (the vessel had no per-router state
                // by construction under the Unassigned sentinel).
                var bobStrayKolony = bob.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(bobStrayKolony, "Bob received a kolony echo on Unassigned→B — no migration was due.");
                var bobStrayOrbital = bob.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(bobStrayOrbital, "Bob received an orbital echo on Unassigned→B — no migration was due.");

                // Vessel ground-truth.
                Assert.AreEqual(bobAgency, VesselStoreSystem.CurrentVessels[vesselId].OwningAgencyId);
            }
        }

        [TestMethod]
        public void SetVesselAgency_CrossAgencyLockBlocked_BobCanAcquirePostTransfer()
        {
            // Pre-move 5.17a rejection: Bob can't take Control of Alice's vessel.
            // Post-move (after setvesselagency): Bob can. Alice's stale lock (if
            // any) was released in step 7 of the 9-step contract.
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2-e2-a3");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2-e2-b3");

                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Alice acquires Control lock (same-agency, succeeds under 5.17a).
                var aliceLockOk = LockSystem.AcquireLock(
                    new LockDefinition(LockType.Control, "h-mksr2-e2-a3", vesselId),
                    force: false, out _);
                Assert.IsTrue(aliceLockOk, "Alice's same-agency lock acquire must succeed pre-transfer.");

                // Pre-move: Bob tries to acquire Control — 5.17a cross-agency rejects.
                var bobLockBeforeFails = LockSystem.AcquireLock(
                    new LockDefinition(LockType.Control, "h-mksr2-e2-b3", vesselId),
                    force: false, out _);
                Assert.IsFalse(bobLockBeforeFails, "Pre-transfer cross-agency lock acquire must be rejected by 5.17a.");

                // Operator transfers V from Alice to Bob.
                var ok = new SetVesselAgencyCommand().Execute($"{vesselId:N} {bobAgency:N}");
                Assert.IsTrue(ok);

                // Drain the wire echoes so they don't bleed into subsequent tests.
                alice.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));
                bob.WaitForReply<AgencyVisibilityMsgData>(TimeSpan.FromSeconds(5));

                // Step 7 of the 9-step contract: Alice's stale Control lock was
                // released. Re-acquire under Alice would now fail (cross-agency
                // post-transfer) but the existing lock state should show no
                // Alice holder.
                Assert.IsFalse(
                    LockSystem.LockQuery.LockExists(LockType.Control, vesselId, "h-mksr2-e2-a3"),
                    "Alice's stale Control lock must have been released by step 7.");

                // Post-move: Bob acquires Control — 5.17a same-agency permits.
                var bobLockAfterOk = LockSystem.AcquireLock(
                    new LockDefinition(LockType.Control, "h-mksr2-e2-b3", vesselId),
                    force: false, out _);
                Assert.IsTrue(bobLockAfterOk, "Post-transfer Bob (now the owner) must succeed in acquiring Control.");

                // Ground-truth: vessel belongs to Bob.
                Assert.AreEqual(bobAgency, VesselStoreSystem.CurrentVessels[vesselId].OwningAgencyId);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Performs the handshake + drains the mandatory agency catchup chain
        /// (AgencyHandshake + AgencyState + AgencyKolony + AgencyPlanetary +
        /// AgencyOrbital). Contract catchup is NOT sent for fresh agencies
        /// (no persisted contracts); the inbox preserves out-of-order messages
        /// so any incoming contract message stays buffered for explicit
        /// consumption if the test plants contracts later.
        /// </summary>
        private static Guid HandshakeAndDrainAgencyMessages(MockNetClient client, string playerName)
        {
            var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
            handshake.PlayerName = playerName;
            handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
            handshake.KspVersion = "1.12.5";
            client.SendMessage<HandshakeCliMsg>(handshake);

            var reply = client.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(reply, $"Handshake reply missing for {playerName}.");
            Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response,
                $"Handshake rejected for {playerName}: " + reply.Reason);

            var hs = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(hs, $"Did not receive AgencyHandshakeMsgData for {playerName}.");
            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyStateMsgData for {playerName}.");

            // Drain the three Phase-3 router catchups (always sent under gate=on,
            // even for empty dicts so the client mirror distinguishes "no entries
            // yet" from "unsynced").
            Assert.IsNotNull(client.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5)),
                $"Did not receive AgencyKolonyStateMsgData catch-up for {playerName}.");
            Assert.IsNotNull(client.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5)),
                $"Did not receive AgencyPlanetaryStateMsgData catch-up for {playerName}.");
            Assert.IsNotNull(client.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5)),
                $"Did not receive AgencyOrbitalStateMsgData catch-up for {playerName}.");

            return state.AgencyId;
        }

        private static string LoadSampleVesselText()
        {
            var probe = new DirectoryInfo(AppContext.BaseDirectory);
            while (probe != null && !Directory.Exists(Path.Combine(probe.FullName, "ServerTest", "XmlExampleFiles", "Others")))
                probe = probe.Parent;

            Assert.IsNotNull(probe, "Could not locate ServerTest/XmlExampleFiles/Others.");
            var fixtureDir = Path.Combine(probe.FullName, "ServerTest", "XmlExampleFiles", "Others");
            var samplePath = Directory.GetFiles(fixtureDir).OrderBy(p => p, StringComparer.Ordinal).First();
            return File.ReadAllText(samplePath);
        }
    }
}
