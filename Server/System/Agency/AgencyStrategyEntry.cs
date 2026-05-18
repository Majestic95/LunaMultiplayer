using System;

namespace Server.System.Agency
{
    /// <summary>Stage 5.17e-6 — per-agency strategy record. Same shape as
    /// other Agency*Entry types: stable key (Strategy name) + decompressed
    /// wire payload + NumBytes for buffer-clamp safety. Persisted under
    /// STRATEGIES/STRATEGY child nodes; spliced into outgoing StrategySystem
    /// scenario as <c>STRATEGIES { STRATEGY { ... } }</c> by the projector.</summary>
    public class AgencyStrategyEntry
    {
        public string StrategyName { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int NumBytes { get; set; }
    }
}
