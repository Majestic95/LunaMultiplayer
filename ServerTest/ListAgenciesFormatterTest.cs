using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.18d slice (d) <see cref="ListAgenciesFormatter"/>. Pins
    /// every branch of the formatter against synthetic rows — the console output IS the
    /// API contract for the Stage 5.18+ GUI launcher (which parses server stdout). Format
    /// drift caught here saves a downstream parser regression.
    ///
    /// <para>The test ships a reference quote-aware tokenizer (<see cref="RowTokenizer"/>)
    /// matching the contract documented on <see cref="ListAgenciesFormatter"/>. Every
    /// row-style test routes the line through it so a downstream parser written against
    /// the same rule can confidently consume the format — and so we catch regressions
    /// where a naive <c>line.Split(' ')</c> would silently mis-tokenize quoted strings
    /// containing spaces.</para>
    /// </summary>
    [TestClass]
    public class ListAgenciesFormatterTest
    {
        private CultureInfo _originalCulture;

        [TestInitialize]
        public void Setup()
        {
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        }

        [TestCleanup]
        public void Teardown()
        {
            Thread.CurrentThread.CurrentCulture = _originalCulture;
        }

        [TestMethod]
        public void EveryLine_CarriesTheBlockIdTag()
        {
            // CL1: LunaLog prepends [HH:mm:ss][LMP]:  to every line, destroying any
            // leading-indent block-id contract. Every emitted line must carry the
            // [fix:per-agency-career] tag so the GUI parser can correlate.
            var lines = ListAgenciesFormatter.Format(
                rows: new[] { NewRow("Alice", "Alice Space Agency", funds: 1d, sci: 2d, rep: 3d, vessels: 4) },
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 2,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            Assert.IsTrue(lines.Count >= 3, "expected start + row + unassigned + end");
            foreach (var line in lines)
                StringAssert.StartsWith(line, ListAgenciesFormatter.Tag);
        }

        [TestMethod]
        public void GateOff_NoUnassigned_SingleDisabledLine()
        {
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: false,
                gateActive: false,
                acceptedLossOverrideSet: false).ToList();

            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "disabled: PerAgencyCareer=false");
        }

        [TestMethod]
        public void GateOff_StrandedStamps_EmitsStatusAndRecoveryWithThreePaths()
        {
            // S1 fix: the stranded-stamps recovery text must list all three paths from
            // WarnAboutStrandedAgencyStampsIfGateOff, not just transferagency.
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 3,
                perAgencyConfigured: false,
                gateActive: false,
                acceptedLossOverrideSet: false).ToList();

            Assert.AreEqual(3, lines.Count, "disabled + stranded-stamps + recovery");
            StringAssert.Contains(lines[1], "stranded-stamps vessels=3");
            // All three recovery paths surfaced in preference order — least destructive first.
            StringAssert.Contains(lines[2], "keep Universe/Agencies/ intact");
            StringAssert.Contains(lines[2], ".bak");
            StringAssert.Contains(lines[2], "transferagency");
        }

        [TestMethod]
        public void GateConfiguredButInactive_RecoveryMentionsBothResolutionPaths()
        {
            // S2 fix: the inactive-state hint must mention BOTH the GameMode=Career path
            // and the PerAgencyCareer=false path, matching the boot warning at
            // AgencySystem.cs:165-173. Otherwise operators in Sandbox mode who configured
            // per-agency by accident have no signal that disabling it is a valid fix.
            var rows = new[] { NewRow("Alice", "Alice Space Agency") };
            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: false,
                acceptedLossOverrideSet: false).ToList();

            var inactiveLine = lines.Single(l => l.Contains(" inactive:"));
            StringAssert.Contains(inactiveLine, "GameMode=Career");
            StringAssert.Contains(inactiveLine, "PerAgencyCareer=false");
            // The start-of-block line carries state=inactive so the GUI knows the rows are
            // disk-loaded diagnostics, not live state.
            var startLine = lines.Single(l => l.Contains(" registry start"));
            StringAssert.Contains(startLine, "state=inactive");
        }

        [TestMethod]
        public void GateActive_RegistryHasStartStateAndTerminatorEnd()
        {
            // CL2: block-id and terminator. The GUI parser must see exactly one start +
            // one end per invocation so it can frame the rows between them.
            var rows = new[] { NewRow("Alice", "Alice Space Agency") };
            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var startCount = lines.Count(l => l.Contains(" registry start"));
            var endCount = lines.Count(l => l.Contains(" registry end"));
            Assert.AreEqual(1, startCount, "exactly one start line");
            Assert.AreEqual(1, endCount, "exactly one end line");
            StringAssert.Contains(lines.First(l => l.Contains(" registry start")), "state=live");
            StringAssert.Contains(lines.First(l => l.Contains(" registry start")), "agencies=1");
            StringAssert.Contains(lines.Last(l => l.Contains(" registry end")), "rows=1");
            StringAssert.Contains(lines.Last(l => l.Contains(" registry end")), "orphans=0");
            StringAssert.Contains(lines.Last(l => l.Contains(" registry end")), "unassigned=0");
        }

        [TestMethod]
        public void GateActive_ZeroRows_StartAndEndStillEmittedForBlockFraming()
        {
            // GUI parser relies on start/end framing even for empty results — gives a
            // stable signal that "yes, the command ran, registry is just empty".
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            Assert.AreEqual(2, lines.Count, "start + end only");
            StringAssert.Contains(lines[0], "registry start");
            StringAssert.Contains(lines[0], "agencies=0");
            StringAssert.Contains(lines[1], "registry end");
            StringAssert.Contains(lines[1], "rows=0");
        }

        [TestMethod]
        public void GateActive_OrphanAgencies_SurfacedAsTaggedRows()
        {
            // M1 fix: orphan-agency vessels (referenced by vessels but not in registry)
            // were silently dropped from both the per-row count and the unassigned
            // tally in the initial implementation. They MUST be surfaced — the boot
            // helper WarnAboutOrphanedVessels does so loudly at startup, and operators
            // running /listagencies mid-session to verify post-recovery state need the
            // same visibility.
            var orphanId1 = new Guid("12345678123456781234567812345678");
            var orphanId2 = new Guid("87654321876543218765432187654321");
            var lines = ListAgenciesFormatter.Format(
                rows: new[] { NewRow("Alice", "Alice Space Agency", vessels: 4) },
                orphans: new[]
                {
                    new ListAgenciesFormatter.OrphanRow { OrphanAgencyId = orphanId1, VesselCount = 2 },
                    new ListAgenciesFormatter.OrphanRow { OrphanAgencyId = orphanId2, VesselCount = 5 },
                },
                unassignedVessels: 3,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            // Tighter filter to ' orphan id=' so the orphan-consequence reminder
            // (which also contains the word "orphan" in its prose) is not picked up.
            var orphanLines = lines.Where(l => l.Contains(" orphan id=")).ToList();
            Assert.AreEqual(2, orphanLines.Count);
            // Stable order by AgencyId.
            StringAssert.Contains(orphanLines[0], "id=12345678123456781234567812345678");
            StringAssert.Contains(orphanLines[0], "vessels=2");
            StringAssert.Contains(orphanLines[1], "id=87654321876543218765432187654321");
            StringAssert.Contains(orphanLines[1], "vessels=5");

            // End-line counts include orphans separately from rows so the GUI can
            // expose the distinction in its dashboard.
            var endLine = lines.Single(l => l.Contains(" registry end"));
            StringAssert.Contains(endLine, "rows=1");
            StringAssert.Contains(endLine, "orphans=2");
            StringAssert.Contains(endLine, "unassigned=3");
        }

        [TestMethod]
        public void GateActive_UnassignedTallyOmittedWhenZero()
        {
            // Keep the output quiet when there's nothing to report — operators reading
            // /listagencies on a clean per-agency universe shouldn't see "unassigned
            // vessels=0" noise. The terminator still carries the zero in unassigned=
            // for tooling that wants it.
            var lines = ListAgenciesFormatter.Format(
                rows: new[] { NewRow("Alice", "Alice Space Agency") },
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            Assert.IsFalse(lines.Any(l => l.Contains(" unassigned vessels=")),
                "no unassigned-vessels status line when count is zero");
            StringAssert.Contains(lines.Single(l => l.Contains(" registry end")), "unassigned=0");
        }

        [TestMethod]
        public void Orphans_TriggerOrphanConsequenceReminderLine()
        {
            // [Upgrade-lens v2 N4] One orphan-consequence reminder per block surfaces the
            // lock-rejection + relay-drop consequences at the dashboard surface so the GUI
            // consumer doesn't need to scrape boot logs to know what an orphan means.
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: new[]
                {
                    new ListAgenciesFormatter.OrphanRow
                    {
                        OrphanAgencyId = new Guid("12345678123456781234567812345678"),
                        VesselCount = 2,
                    },
                },
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var consequenceLines = lines.Where(l => l.Contains(" orphan-consequence:")).ToList();
            Assert.AreEqual(1, consequenceLines.Count, "exactly one orphan-consequence reminder per block");
            StringAssert.Contains(consequenceLines[0], "lock acquires");
            StringAssert.Contains(consequenceLines[0], "silently dropped");
            StringAssert.Contains(consequenceLines[0], "transferagency");
        }

        [TestMethod]
        public void NoOrphans_OrphanConsequenceReminderOmitted()
        {
            var lines = ListAgenciesFormatter.Format(
                rows: new[] { NewRow("Alice", "Alice Space Agency") },
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            Assert.IsFalse(lines.Any(l => l.Contains(" orphan-consequence:")),
                "no orphan-consequence line when orphan count is zero");
        }

        [TestMethod]
        public void FirstConnectPending_FiresOnUpgradeSignature_OverrideFalse()
        {
            // [Upgrade-lens v2 N1+N3] gate active + zero rows + (orphans + unassigned > 0)
            // is the upgrade-in-place signature. Boot's RefuseStartupIfUpgradeHazardWithoutOverride
            // refuses unless the operator opted in; mid-session this line is the verification
            // surface. override=false means the boot refusal was bypassed at runtime (rare).
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: new[]
                {
                    new ListAgenciesFormatter.OrphanRow
                    {
                        OrphanAgencyId = new Guid("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                        VesselCount = 3,
                    },
                },
                unassignedVessels: 2,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var pendingLine = lines.Single(l => l.Contains(" first-connect-pending "));
            // hazard = unassignedVessels (2) + sum(orphan vessels) (3) = 5
            StringAssert.Contains(pendingLine, "hazard=5");
            StringAssert.Contains(pendingLine, "override=false");
        }

        [TestMethod]
        public void FirstConnectPending_FiresOnUpgradeSignature_OverrideTrue()
        {
            // override=true is the deliberate operator-opted-in accepted-loss state.
            // Operators returning to the server days later need a signal they're in this mode.
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 4,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: true).ToList();

            var pendingLine = lines.Single(l => l.Contains(" first-connect-pending "));
            StringAssert.Contains(pendingLine, "hazard=4");
            StringAssert.Contains(pendingLine, "override=true");
        }

        [TestMethod]
        public void FirstConnectPending_OmittedWhenRegistryIsPopulated()
        {
            // The signature is rows.Count == 0 — once any agency has registered, the
            // upgrade-pending state is over and the line stops being relevant.
            var lines = ListAgenciesFormatter.Format(
                rows: new[] { NewRow("Alice", "Alice Space Agency") },
                orphans: new[]
                {
                    new ListAgenciesFormatter.OrphanRow
                    {
                        OrphanAgencyId = new Guid("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                        VesselCount = 2,
                    },
                },
                unassignedVessels: 3,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: true).ToList();

            Assert.IsFalse(lines.Any(l => l.Contains(" first-connect-pending ")),
                "first-connect-pending line gates on rows.Count == 0");
        }

        [TestMethod]
        public void FirstConnectPending_OmittedOnPristineUniverse()
        {
            // Zero rows + zero unassigned + zero orphans = pristine universe (gate just
            // turned on, no one connected yet). No diagnostic noise; operator sees only
            // the empty registry start/end frame.
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: true).ToList();

            Assert.IsFalse(lines.Any(l => l.Contains(" first-connect-pending ")),
                "first-connect-pending requires non-zero hazard count");
        }

        [TestMethod]
        public void StrandedRecoveryText_MentionsBakRotationCopyClearly()
        {
            // [Upgrade-lens v2 N5] Aligns the wording so an operator with .bak surviving
            // but canonical deleted picks the right recovery path.
            var lines = ListAgenciesFormatter.Format(
                rows: Array.Empty<ListAgenciesFormatter.AgencyRow>(),
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 1,
                perAgencyConfigured: false,
                gateActive: false,
                acceptedLossOverrideSet: false).ToList();

            var recovery = lines.Single(l => l.Contains(" disabled-recovery:"));
            StringAssert.Contains(recovery, ".bak rotation copy");
            StringAssert.Contains(recovery, "canonical file was deleted");
        }

        [TestMethod]
        public void RowOrdering_ByDisplayNameThenAgencyId()
        {
            // Stable ordering matters because GUI diff-refresh logic depends on it
            // (otherwise rows churn position between identical calls and the table
            // animates needlessly).
            var aliceId1 = new Guid("11111111111111111111111111111111");
            var aliceId2 = new Guid("22222222222222222222222222222222");
            var rows = new[]
            {
                NewRow("Bob",    "Zenith Aerospace",   id: new Guid("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
                NewRow("Carol",  "Apex Industries",    id: new Guid("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
                NewRow("Alice2", "Alice Space Agency", id: aliceId2),
                NewRow("Alice1", "Alice Space Agency", id: aliceId1),
            };
            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var rowLines = lines.Where(l => l.Contains(" row ")).ToList();
            Assert.AreEqual(4, rowLines.Count);
            // Alice Space Agency × 2 (id1 < id2) → Apex → Zenith.
            StringAssert.Contains(rowLines[0], "id=11111111111111111111111111111111");
            StringAssert.Contains(rowLines[1], "id=22222222222222222222222222222222");
            StringAssert.Contains(rowLines[2], "display=\"Apex Industries\"");
            StringAssert.Contains(rowLines[3], "display=\"Zenith Aerospace\"");
        }

        [TestMethod]
        public void NumericRoundTrip_PreservedUnderNonInvariantCulture()
        {
            var rows = new[] { NewRow("Alice", "Alice Space Agency",
                funds: 22943.5d, sci: 0.000001d, rep: -3.14d) };

            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var rowLine = lines.Single(l => l.Contains(" row "));
            // Quote-aware tokenization — proves the consumer parse rule works on real output.
            var tokens = RowTokenizer.Parse(StripTag(rowLine));
            Assert.AreEqual("22943.5", tokens["funds"]);
            Assert.AreEqual("-3.14", tokens["rep"]);
            // Round-trip via InvariantCulture as the contract documents.
            Assert.AreEqual(
                22943.5d,
                double.Parse(tokens["funds"], NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void DisplayNameWithSpaces_SurvivesQuoteAwareTokenization()
        {
            // CL3 fix: the original "Apex Industries" display name was the canonical
            // failure case for naive line.Split(' ') parsers. The output is unchanged
            // (quoted with spaces in the middle), but this test PROVES a parser
            // following the documented rule recovers it correctly.
            var rows = new[] { NewRow("Carol", "Apex Industries") };
            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var rowLine = lines.Single(l => l.Contains(" row "));
            var tokens = RowTokenizer.Parse(StripTag(rowLine));
            Assert.AreEqual("Carol", tokens["owner"]);
            Assert.AreEqual("Apex Industries", tokens["display"]);
        }

        [TestMethod]
        public void StringFields_EmbeddedQuotesAndBackslashes_RoundTripThroughTokenizer()
        {
            var rows = new[] { NewRow(
                owner: "Alice \"Ace\" Bishop",
                display: "C:\\path\\to\\agency \"main\"") };

            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var rowLine = lines.Single(l => l.Contains(" row "));
            var tokens = RowTokenizer.Parse(StripTag(rowLine));
            Assert.AreEqual("Alice \"Ace\" Bishop", tokens["owner"]);
            Assert.AreEqual("C:\\path\\to\\agency \"main\"", tokens["display"]);
        }

        [TestMethod]
        public void EmptyOrNullStrings_RenderAsEmptyQuotedTokens()
        {
            var rows = new[] { NewRow(owner: null, display: null) };
            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var rowLine = lines.Single(l => l.Contains(" row "));
            var tokens = RowTokenizer.Parse(StripTag(rowLine));
            Assert.AreEqual("", tokens["owner"]);
            Assert.AreEqual("", tokens["display"]);
        }

        [TestMethod]
        public void RowShape_StableKeyOrder_MatchesParserContract()
        {
            // Pin canonical key order so a regression that shuffles fields is caught.
            var rows = new[] { NewRow("Alice", "Alice Space Agency",
                funds: 1d, sci: 2d, rep: 3d, vessels: 4) };
            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var row = lines.Single(l => l.Contains(" row "));
            // After "[fix:per-agency-career] row " the keys appear in canonical order.
            var keyOrder = new[] { "id=", "owner=", "display=", "funds=", "sci=", "rep=", "vessels=" };
            var lastIndex = row.IndexOf(" row ", StringComparison.Ordinal);
            Assert.IsTrue(lastIndex >= 0, "row line carries the ' row ' marker");
            foreach (var key in keyOrder)
            {
                var idx = row.IndexOf(key, lastIndex, StringComparison.Ordinal);
                Assert.IsTrue(idx > lastIndex, $"key '{key}' should follow the prior keys in canonical order");
                lastIndex = idx;
            }
        }

        [TestMethod]
        public void NonFiniteScalars_DocumentedToEmitInfinityAndNaNTokens()
        {
            // The "R" format spec round-trips through double.Parse(..., NumberStyles.Float,
            // InvariantCulture). Hand-edited agency files (AgencyState.Parse is permissive)
            // could produce non-finite values; the formatter doesn't scrub them — it
            // surfaces them so the operator sees the problem instead of silently rendering
            // "0". GUI consumers should detect Infinity / NaN and present as "corrupt" in
            // their UI.
            var rows = new[] { NewRow("Alice", "Alice Space Agency",
                funds: double.PositiveInfinity,
                sci: double.NegativeInfinity,
                rep: double.NaN) };

            var lines = ListAgenciesFormatter.Format(
                rows: rows,
                orphans: Array.Empty<ListAgenciesFormatter.OrphanRow>(),
                unassignedVessels: 0,
                perAgencyConfigured: true,
                gateActive: true,
                acceptedLossOverrideSet: false).ToList();

            var rowLine = lines.Single(l => l.Contains(" row "));
            var tokens = RowTokenizer.Parse(StripTag(rowLine));
            Assert.AreEqual("Infinity", tokens["funds"]);
            Assert.AreEqual("-Infinity", tokens["sci"]);
            Assert.AreEqual("NaN", tokens["rep"]);
            // Confirm the round-trip path the contract recommends.
            Assert.IsTrue(double.IsPositiveInfinity(double.Parse(tokens["funds"], NumberStyles.Float, CultureInfo.InvariantCulture)));
            Assert.IsTrue(double.IsNaN(double.Parse(tokens["rep"], NumberStyles.Float, CultureInfo.InvariantCulture)));
        }

        // ----- helpers -----

        /// <summary>
        /// Strip the <c>[fix:per-agency-career] </c> prefix from a line so the tokenizer
        /// sees just the kind-keyword + key=value tokens that follow. Real GUI consumers
        /// match the tag with a regex / startswith first, then tokenize the remainder.
        /// </summary>
        private static string StripTag(string line) =>
            line.StartsWith(ListAgenciesFormatter.Tag, StringComparison.Ordinal)
                ? line.Substring(ListAgenciesFormatter.Tag.Length).TrimStart()
                : line;

        private static ListAgenciesFormatter.AgencyRow NewRow(
            string owner = "Alice",
            string display = "Alice Space Agency",
            double funds = 0d,
            double sci = 0d,
            double rep = 0d,
            int vessels = 0,
            Guid? id = null)
            => new ListAgenciesFormatter.AgencyRow
            {
                AgencyId = id ?? Guid.NewGuid(),
                OwningPlayerName = owner,
                DisplayName = display,
                Funds = funds,
                Science = sci,
                Reputation = rep,
                VesselCount = vessels,
            };

        /// <summary>
        /// Reference quote-aware tokenizer matching the parse contract documented on
        /// <see cref="ListAgenciesFormatter"/>. Shipped as part of the test suite so a
        /// future GUI / tooling author can read this file and lift a working
        /// implementation directly. NOT production code — keep it minimal and obvious;
        /// production parsers can choose a more robust style (regex, JSON, etc.).
        /// </summary>
        internal static class RowTokenizer
        {
            public static IDictionary<string, string> Parse(string content)
            {
                // Discard a leading "kind keyword" (e.g. "row", "orphan", "registry start").
                // Tokenizer is permissive — it stops at the first key=value pair and
                // ignores anything before. Production parsers may want to retain the
                // kind keyword as a separate field.
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                var i = 0;
                while (i < content.Length)
                {
                    while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
                    if (i >= content.Length) break;

                    // Read a candidate "key=" run. If we hit whitespace before '=', this
                    // run was a bare keyword (e.g. "row") and we skip it.
                    var keyStart = i;
                    while (i < content.Length && content[i] != '=' && !char.IsWhiteSpace(content[i])) i++;
                    if (i >= content.Length || char.IsWhiteSpace(content[i]))
                        continue;
                    if (content[i] != '=')
                        continue;
                    var key = content.Substring(keyStart, i - keyStart);
                    i++; // consume '='

                    string value;
                    if (i < content.Length && content[i] == '"')
                    {
                        i++; // consume opening quote
                        var sb = new StringBuilder();
                        while (i < content.Length)
                        {
                            if (content[i] == '\\' && i + 1 < content.Length)
                            {
                                // \" → " and \\ → \ (the documented unescape order).
                                sb.Append(content[i + 1]);
                                i += 2;
                            }
                            else if (content[i] == '"')
                            {
                                i++; // consume closing quote
                                break;
                            }
                            else
                            {
                                sb.Append(content[i]);
                                i++;
                            }
                        }
                        value = sb.ToString();
                    }
                    else
                    {
                        var valStart = i;
                        while (i < content.Length && !char.IsWhiteSpace(content[i])) i++;
                        value = content.Substring(valStart, i - valStart);
                    }
                    result[key] = value;
                }
                return result;
            }
        }
    }
}
