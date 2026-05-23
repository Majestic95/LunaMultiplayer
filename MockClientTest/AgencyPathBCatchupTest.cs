using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Scenario;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace MockClientTest
{
    /// <summary>
    /// [Catch-up-baseline fix 2026-05-22] End-to-end tests for the Path B
    /// catch-up bug: under <c>PerAgencyCareer=true</c> on a fresh universe,
    /// per-agency state (SCANsat / DMagic) was correctly persisted in
    /// <see cref="AgencyState"/> on disk but never projected back to
    /// reconnecting clients because <see cref="ScenarioStoreSystem.CurrentScenarios"/>
    /// never received a baseline entry. The router suppresses the shared-store
    /// insert under gate=on, and no other code path populates it for mod
    /// scenarios like <c>SCANcontroller</c> / <c>DMScienceScenario</c>.
    ///
    /// <para>The fix lives in <see cref="AgencyScanRouter.SeedBaselineIfMissing"/>
    /// + <see cref="AgencyDMagicRouter.SeedBaselineIfMissing"/>. These tests
    /// exercise the full wire path: first client connects → broadcasts a
    /// scenario blob with player-progress data → disconnects → second client
    /// reconnects with the same player name → HandshakeSystem's catch-up
    /// (<see cref="ScenarioSystem.SendScenariosToClient"/>) ships a
    /// <see cref="ScenarioDataMsgData"/> that includes the persisted player-
    /// progress entries spliced onto the baseline.</para>
    ///
    /// <para><b>Bug reproduction:</b> reverting <c>SeedBaselineIfMissing</c>
    /// would make these tests fail at the "catch-up arrived with non-empty
    /// body data" assertions — the catch-up either wouldn't arrive at all
    /// (no matching key in CurrentScenarios) or would be missing the spliced
    /// player-progress.</para>
    /// </summary>
    [TestClass]
    public class AgencyPathBCatchupTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        // -------------------------------------------------------------------
        // S2 SCANsat catch-up
        // -------------------------------------------------------------------

        [TestMethod]
        public void Scansat_OwnerReconnect_ReceivesCatchupWithBodyCoverage()
        {
            // The reported bug from the cohort soak: player launches a SCANsat
            // satellite into Minmus orbit, scans accumulate during the session,
            // logs out, logs back in, map is RESET — all info gone.
            //
            // Pre-fix: CurrentScenarios["SCANcontroller"] is never populated on
            // a fresh per-agency universe → SendScenariosToClient finds no key
            // → catch-up sends nothing → client KSP loads default empty
            // SCANcontroller scenario.
            //
            // Post-fix: SeedBaselineIfMissing populates the baseline on first
            // broadcast → catch-up finds the key → projector splices Alice's
            // per-agency Coverage data onto the baseline → catch-up carries
            // the Minmus body record.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            const string playerName = "pb-scan-own";
            const string targetBody = "Minmus";

            using (var first = new MockNetClient())
            {
                Assert.IsTrue(first.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(first, playerName);

                var aliceAgency = AgencySystem.AgencyByPlayerName[playerName];

                first.SendMessage<ScenarioCliMsg>(BuildScansatBroadcast(targetBody));

                // Poll until the router's Task.Run completes — visible via the
                // Coverage dictionary being populated. Without this we'd race
                // the disconnect against the persistence write.
                Assert.IsTrue(
                    WaitFor(() => AgencySystem.Agencies[aliceAgency].Coverage.ContainsKey(targetBody),
                            TimeSpan.FromSeconds(5)),
                    $"Router did not persist {targetBody} coverage to Alice's agency state.");

                Assert.IsTrue(ScenarioStoreSystem.CurrentScenarios.ContainsKey("SCANcontroller"),
                    "Baseline-seed fix should have populated CurrentScenarios[\"SCANcontroller\"] on first inbound — without this the catch-up has nothing to project against.");
            }

            using (var second = new MockNetClient())
            {
                Assert.IsTrue(second.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(second, playerName);

                var catchup = second.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(catchup,
                    "Reconnecting owner did NOT receive a ScenarioDataMsgData catch-up — under the pre-fix code path, SendScenariosToClient returns early when no matching key exists in CurrentScenarios.");

                var scansatPayload = FindScenarioPayload(catchup, "SCANcontroller");
                Assert.IsNotNull(scansatPayload,
                    "Catch-up did NOT include a SCANcontroller scenario — the user-reported bug: per-agency state on disk is invisible to the reconnecting owner.");

                Assert.IsTrue(scansatPayload.Contains($"Name = {targetBody}"),
                    $"Catch-up SCANcontroller did not carry {targetBody} body entry. Payload:\n{scansatPayload}");
            }
        }

        [TestMethod]
        public void Scansat_CrossAgencyPrivacy_PeerCannotSeeOwnerCoverage()
        {
            // Defensive: under per-agency mode, Bob (different agency) must NOT
            // see Alice's coverage in his catch-up. The baseline is shared
            // (it has no Body children by construction) but the projector
            // splices each agency's own data onto it — Alice gets Minmus,
            // Bob gets empty Progress.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "pb-scan-alice");
                var aliceAgency = AgencySystem.AgencyByPlayerName["pb-scan-alice"];

                alice.SendMessage<ScenarioCliMsg>(BuildScansatBroadcast("Minmus"));
                Assert.IsTrue(
                    WaitFor(() => AgencySystem.Agencies[aliceAgency].Coverage.ContainsKey("Minmus"),
                            TimeSpan.FromSeconds(5)));
            }

            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "pb-scan-bob");

                var catchup = bob.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(catchup, "Bob did not receive any handshake-time catch-up at all.");

                var bobsScansat = FindScenarioPayload(catchup, "SCANcontroller");
                Assert.IsNotNull(bobsScansat,
                    "Bob's catch-up should include SCANcontroller (empty Progress is correct, but the key must be present).");

                Assert.IsFalse(bobsScansat.Contains("Name = Minmus"),
                    "Cross-agency privacy violated: Bob saw Alice's Minmus coverage in his catch-up. Payload:\n" + bobsScansat);
            }
        }

        // -------------------------------------------------------------------
        // S4 DMagic catch-up
        // -------------------------------------------------------------------

        [TestMethod]
        public void DMagic_OwnerReconnect_ReceivesCatchupWithAsteroidScience()
        {
            // Same bug shape as SCANsat, mirrored fix. DMScienceScenario is
            // also Path B with no operator-seed and no other path to populate
            // CurrentScenarios — so reconnect catch-up shipped nothing for it
            // pre-fix.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            const string playerName = "pb-dmagic-own";
            const string asteroidTitle = "Ast-42";

            using (var first = new MockNetClient())
            {
                Assert.IsTrue(first.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(first, playerName);

                var aliceAgency = AgencySystem.AgencyByPlayerName[playerName];

                first.SendMessage<ScenarioCliMsg>(BuildDMagicBroadcast(asteroidTitle));

                Assert.IsTrue(
                    WaitFor(() => AgencySystem.Agencies[aliceAgency].DMagicAsteroidScience.ContainsKey(asteroidTitle),
                            TimeSpan.FromSeconds(5)),
                    "Router did not persist DMagic asteroid science to Alice's agency state.");

                Assert.IsTrue(ScenarioStoreSystem.CurrentScenarios.ContainsKey("DMScienceScenario"),
                    "DMagic baseline-seed fix should have populated CurrentScenarios[\"DMScienceScenario\"] on first inbound.");
            }

            using (var second = new MockNetClient())
            {
                Assert.IsTrue(second.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(second, playerName);

                var catchup = second.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(catchup,
                    "Reconnecting owner did NOT receive a ScenarioDataMsgData catch-up under per-agency DMagic.");

                var dmPayload = FindScenarioPayload(catchup, "DMScienceScenario");
                Assert.IsNotNull(dmPayload,
                    "Catch-up did NOT include a DMScienceScenario — symmetric bug to the SCANsat catch-up gap.");

                Assert.IsTrue(dmPayload.Contains($"title = {asteroidTitle}"),
                    $"Catch-up DMScienceScenario did not carry asteroid {asteroidTitle}. Payload:\n{dmPayload}");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static ScenarioDataMsgData BuildScansatBroadcast(string bodyName)
        {
            // Minimal SCANcontroller blob: Progress.Body with required fields
            // per SCANsat OnLoad. Field shape matches
            // AgencyScanRouter.UpsertCoverageEntries.
            var body =
                "Progress\n" +
                "{\n" +
                "  Body\n" +
                "  {\n" +
                "    Name = " + bodyName + "\n" +
                "    Disabled = False\n" +
                "    MinHeightRange = 0\n" +
                "    MaxHeightRange = 1000\n" +
                "    PaletteName = Default\n" +
                "    PaletteSize = 7\n" +
                "    PaletteReverse = False\n" +
                "    PaletteDiscrete = False\n" +
                "    Map = ZmFrZWJsb2I=\n" +
                "  }\n" +
                "}\n";
            return BuildScenarioDataMsg("SCANcontroller", body);
        }

        private static ScenarioDataMsgData BuildDMagicBroadcast(string asteroidTitle)
        {
            // Minimal DMScienceScenario blob: Asteroid_Science.DM_Science with
            // the title-keyed shape AgencyDMagicRouter.UpsertAsteroidScienceEntries
            // accepts.
            var body =
                "Asteroid_Science\n" +
                "{\n" +
                "  DM_Science\n" +
                "  {\n" +
                "    title = " + asteroidTitle + "\n" +
                "    bsv = 1\n" +
                "    scv = 0.5\n" +
                "    sci = 5\n" +
                "    cap = 10\n" +
                "  }\n" +
                "}\n";
            return BuildScenarioDataMsg("DMScienceScenario", body);
        }

        private static ScenarioDataMsgData BuildScenarioDataMsg(string moduleName, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var info = new ScenarioInfo
            {
                Module = moduleName,
                Data = bytes,
                NumBytes = bytes.Length,
            };
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ScenarioDataMsgData>();
            msg.ScenariosData = new[] { info };
            msg.ScenarioCount = 1;
            return msg;
        }

        /// <summary>
        /// Scan a ScenarioDataMsgData reply for the named scenario module and
        /// return its UTF-8-decoded payload, or null if not present.
        /// </summary>
        private static string FindScenarioPayload(ScenarioDataMsgData reply, string moduleName)
        {
            for (var i = 0; i < reply.ScenarioCount; i++)
            {
                var info = reply.ScenariosData[i];
                if (info.Module != moduleName) continue;
                return Encoding.UTF8.GetString(info.Data, 0, info.NumBytes);
            }
            return null;
        }

        /// <summary>
        /// Poll a predicate until it returns true or the timeout elapses.
        /// Returns true if the predicate was satisfied within the window.
        /// Used to wait for the router's Task.Run to complete before
        /// proceeding — the router does not echo to the sender for Path B
        /// scenarios, so there's no wire signal to wait on directly.
        /// </summary>
        private static bool WaitFor(Func<bool> predicate, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    if (predicate()) return true;
                }
                catch
                {
                    // KeyNotFoundException etc. while the agency is still
                    // mid-registration — keep polling.
                }
                Thread.Sleep(25);
            }
            try { return predicate(); }
            catch { return false; }
        }

        private static void HandshakeAndDrainAgencyMessages(MockNetClient client, string playerName)
        {
            HandshakeWithoutAgency(client, playerName);
            // Drain the auto-registration AgencyHandshake + AgencyState so the
            // subsequent assertions about ScenarioDataMsgData aren't racing
            // the handshake-time agency wire.
            var hs = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(hs, $"Did not receive AgencyHandshake for {playerName}.");
            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyState for {playerName}.");
        }

        private static void HandshakeWithoutAgency(MockNetClient client, string playerName)
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
        }
    }
}