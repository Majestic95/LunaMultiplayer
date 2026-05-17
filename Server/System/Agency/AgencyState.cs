using LunaConfigNode.CfgNode;
using Server.Context;
using System;
using System.Globalization;
using System.IO;

namespace Server.System.Agency
{
    /// <summary>
    /// Pure data class representing one per-agency career on the server. Persisted as a
    /// ConfigNode-format text file at <c>Universe/Agencies/{AgencyId}.txt</c>. Lifecycle —
    /// registration, in-memory registry, periodic save — lives in <c>AgencySystem</c>
    /// (Stage 5.15a); this class is just the data + ConfigNode round-trip.
    ///
    /// Stage 5.14c scope is scalar fields only (Id, OwningPlayerName, DisplayName, Funds,
    /// Science, Reputation). The richer dictionaries / lists from spec §3 (TechTree,
    /// KerbalRoster, FacilityLevels, Contracts, Strategies, WorldFirsts, Achievements)
    /// land in their respective later Stage 5 steps as the matching wire / projection
    /// surfaces come online — each will append a child ConfigNode to the format defined
    /// here. The format is forward-compatible by design: unknown fields are tolerated
    /// (the parser simply doesn't read them) and missing fields default to their C# zero
    /// values so older files round-trip cleanly through a newer binary.
    ///
    /// Filename is the canonical GUID (no player name in path) per spec Q7 sign-off —
    /// survives player renames, eliminates the legacy-filename migration PlagueNZ
    /// shipped to recover from a player-name-keyed scheme.
    /// </summary>
    public class AgencyState
    {
        public Guid AgencyId { get; set; }
        public string OwningPlayerName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double Funds { get; set; }
        public double Science { get; set; }
        public double Reputation { get; set; }

        /// <summary>
        /// Universe-relative folder that holds one ConfigNode-format file per agency.
        /// Created at server boot via <see cref="Server.Context.Universe.CheckUniverse"/>
        /// alongside the other Universe child folders, so <see cref="FileHandler.WriteAtomic"/>
        /// can run from the first <see cref="AgencySystem"/> save without a separate
        /// pre-flight directory check.
        /// </summary>
        public static string AgenciesPath => Path.Combine(ServerContext.UniverseDirectory, "Agencies");

        /// <summary>
        /// Canonical file path for this agency's persisted state. The "N" format specifier
        /// gives a 32-char hex string with no hyphens — round-trips through <see cref="Guid.Parse"/>
        /// either way, and keeps filenames operator-friendly (no path-confusing braces).
        /// </summary>
        public string FilePath => Path.Combine(AgenciesPath, AgencyId.ToString("N", CultureInfo.InvariantCulture) + ".txt");

        /// <summary>
        /// Serializes this agency's scalar state to the bare key-value ConfigNode text format
        /// used by <see cref="LunaConfigNode.CfgNode.ConfigNode"/>. No outer <c>{}</c> wrapper
        /// — matches the convention <see cref="ScenarioStoreSystem.GetScenarioInConfigNodeFormat"/>
        /// established (see its summary for why braces are stripped).
        /// </summary>
        public string Serialize() => ToConfigNode().ToString();

        /// <summary>
        /// Builds a ConfigNode for this agency. Exposed (not just internal-to-Serialize) so
        /// future steps can compose AgencyState fragments into larger wire payloads without
        /// reparsing text.
        /// </summary>
        public ConfigNode ToConfigNode()
        {
            var node = new ConfigNode("") { Name = "AGENCY" };
            node.CreateValue(new CfgNodeValue<string, string>("AgencyId", AgencyId.ToString("N", CultureInfo.InvariantCulture)));
            node.CreateValue(new CfgNodeValue<string, string>("OwningPlayerName", OwningPlayerName ?? string.Empty));
            node.CreateValue(new CfgNodeValue<string, string>("DisplayName", DisplayName ?? string.Empty));
            node.CreateValue(new CfgNodeValue<string, string>("Funds", Funds.ToString("R", CultureInfo.InvariantCulture)));
            node.CreateValue(new CfgNodeValue<string, string>("Science", Science.ToString("R", CultureInfo.InvariantCulture)));
            node.CreateValue(new CfgNodeValue<string, string>("Reputation", Reputation.ToString("R", CultureInfo.InvariantCulture)));
            return node;
        }

        /// <summary>
        /// Parses agency state from ConfigNode-format text. Accepts both bare key-value
        /// content (the format <see cref="Serialize"/> produces) and brace-wrapped content
        /// (operators may hand-edit; KSP-side ConfigNode serializers emit braces). Missing
        /// fields default to their C# zero values — forward-compatible for older files
        /// that predate a future field addition.
        /// </summary>
        public static AgencyState Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            var trimmed = text.Trim();
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                trimmed = trimmed.Substring(1, trimmed.Length - 2);

            var node = new ConfigNode(trimmed) { Name = "AGENCY" };
            return FromConfigNode(node);
        }

        /// <summary>
        /// Builds an AgencyState from a pre-parsed ConfigNode. Tolerant of missing fields
        /// (zero/empty defaults) per the forward-compatibility rationale on <see cref="Parse"/>.
        /// Throws only on an unparseable GUID — the AgencyId is the load-bearing identity
        /// and a corrupt one signals a worse-than-default state.
        /// </summary>
        public static AgencyState FromConfigNode(ConfigNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            var state = new AgencyState
            {
                OwningPlayerName = node.GetValue("OwningPlayerName")?.Value ?? string.Empty,
                DisplayName = node.GetValue("DisplayName")?.Value ?? string.Empty,
                Funds = ParseDoubleOrZero(node.GetValue("Funds")?.Value),
                Science = ParseDoubleOrZero(node.GetValue("Science")?.Value),
                Reputation = ParseDoubleOrZero(node.GetValue("Reputation")?.Value),
            };

            var rawId = node.GetValue("AgencyId")?.Value;
            if (string.IsNullOrEmpty(rawId))
                throw new FormatException("AgencyState ConfigNode missing required AgencyId field.");

            state.AgencyId = Guid.Parse(rawId);
            return state;
        }

        private static double ParseDoubleOrZero(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return 0d;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0d;
        }
    }
}
