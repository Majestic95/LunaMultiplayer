using LunaConfigNode.CfgNode;
using Server.Context;
using System;
using System.Collections.Generic;
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
        /// Per-agency contract pool — Active / Completed / Failed / Cancelled /
        /// DeadlineExpired / Withdrawn entries owned by this agency. Offered contracts
        /// are NOT stored here per spec §2 Q6 commitment (a); they live in the shared
        /// scenario pool so Contract Configurator's <c>ContractPreLoader</c> sees the
        /// world it expects. Populated by <see cref="AgencyContractRouter"/>; persisted
        /// as a <c>CONTRACTS</c> child node under the agency's ConfigNode file.
        ///
        /// **Concurrency contract.** Mutations to this list MUST be performed under
        /// <see cref="AgencySystem.GetAgencyLock"/>; the router holds the lock around
        /// the upsert batch, and <see cref="AgencySystem.SaveAgency"/> serialises under
        /// the same lock so a concurrent reader cannot observe a torn list.
        /// </summary>
        public List<AgencyContractEntry> Contracts { get; } = new List<AgencyContractEntry>();

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

            // Contracts persist as a CONTRACTS child node containing one CONTRACT sub-node
            // per entry. Emitted only when non-empty so older / pristine agency files stay
            // visually identical to their 5.14c shape. Bytes are Base64-encoded since
            // ConfigNode values are strings; decompressed form is stored so an operator
            // diffing two AgencyState files can see the actual contract ConfigNode bytes
            // (compression is a wire-only concern handled by ContractInfo at serialize time).
            if (Contracts.Count > 0)
            {
                var contractsRoot = new ConfigNode("") { Name = "CONTRACTS" };
                node.AddNode(contractsRoot);
                foreach (var entry in Contracts)
                {
                    if (entry == null)
                        continue;
                    var contractNode = new ConfigNode("") { Name = "CONTRACT" };
                    contractNode.CreateValue(new CfgNodeValue<string, string>("Guid", entry.ContractGuid.ToString("N", CultureInfo.InvariantCulture)));
                    contractNode.CreateValue(new CfgNodeValue<string, string>("State", entry.State ?? string.Empty));
                    var dataBytes = entry.Data ?? Array.Empty<byte>();
                    var len = Math.Min(entry.NumBytes, dataBytes.Length);
                    var base64 = len > 0 ? Convert.ToBase64String(dataBytes, 0, len) : string.Empty;
                    contractNode.CreateValue(new CfgNodeValue<string, string>("Data", base64));
                    contractsRoot.AddNode(contractNode);
                }
            }

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

            // Forward-compat: CONTRACTS child node is Stage 5.17d addition. Older
            // AgencyState files predate it and load with an empty Contracts list.
            // Per-entry parse failures (malformed Guid / unparseable Base64) are
            // logged via the caller's catch — same per-contract isolation rule the
            // router applies on the wire side.
            var contractsRoot = node.GetNode("CONTRACTS")?.Value;
            if (contractsRoot != null)
            {
                foreach (var contractEntry in contractsRoot.GetNodes("CONTRACT"))
                {
                    var entryNode = contractEntry.Value;
                    var rawGuid = entryNode.GetValue("Guid")?.Value;
                    if (string.IsNullOrEmpty(rawGuid))
                        continue;
                    if (!Guid.TryParse(rawGuid, out var contractGuid))
                        continue;

                    var entry = new AgencyContractEntry
                    {
                        ContractGuid = contractGuid,
                        State = entryNode.GetValue("State")?.Value ?? string.Empty,
                    };

                    var base64 = entryNode.GetValue("Data")?.Value;
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            entry.Data = Convert.FromBase64String(base64);
                            entry.NumBytes = entry.Data.Length;
                        }
                        catch (FormatException)
                        {
                            // Malformed payload: keep entry with empty data so the slot
                            // is observable (operator can see "this contract is broken")
                            // without aborting the parent agency load.
                            entry.Data = Array.Empty<byte>();
                            entry.NumBytes = 0;
                        }
                    }

                    state.Contracts.Add(entry);
                }
            }

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
