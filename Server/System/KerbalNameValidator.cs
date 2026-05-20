using System.IO;

namespace Server.System
{
    /// <summary>
    /// [Stage 6 Phase 6.9-hardening] Wire-boundary validator for kerbal names
    /// arriving on KerbalProtoMsgData + KerbalRemoveMsgData (and operator-
    /// extracted from vessel ConfigNode text on /setvesselagency). The kerbal
    /// name is a sink for two production code paths:
    ///   1. <see cref="KerbalSystem.TryWriteKerbalProtoPerAgency"/> +
    ///      <see cref="KerbalSystem.TryDeleteKerbalPerAgency"/> use it as a
    ///      filename component under <see cref="System.IO.Path.Combine"/>.
    ///   2. <see cref="Server.Command.Command.SetVesselAgencyCommand.ExtractCrewFromVessel"/>
    ///      lifts it from operator-extractable vessel proto text, then feeds
    ///      it back through the same per-agency disk-IO sinks.
    ///
    /// <para><b>Threat model</b> (Stage 6 audit security-lens MUST FIX #1):
    /// a modified LMP client can craft a <see cref="LmpCommon.Message.Data.Kerbal.KerbalProtoMsgData"/>
    /// or <see cref="LmpCommon.Message.Data.Kerbal.KerbalRemoveMsgData"/> with
    /// any string as the kerbal-name field. <see cref="System.IO.Path.Combine"/>
    /// does NOT normalise <c>..</c> segments AND silently discards the first
    /// argument if the second begins with a rooted path (e.g. <c>C:\</c> on
    /// Windows or <c>/</c> on Unix). Without this validator a single crafted
    /// message can produce arbitrary <c>.txt</c> file writes anywhere the
    /// server process has filesystem permissions, or arbitrary delete of any
    /// <c>.txt</c> file under that umbrella (settings, scenario backups, other
    /// agencies' state).</para>
    ///
    /// <para><b>Policy</b> (blacklist, not whitelist — KSP + mod-spawned kerbal
    /// names contain apostrophes / hyphens / spaces / accented letters via mods
    /// like Real Names, so a strict whitelist would false-reject legitimate
    /// names):</para>
    /// <list type="bullet">
    ///   <item>Reject empty or whitespace-only names.</item>
    ///   <item>Reject length &gt; <see cref="MaxLength"/> (64 chars — KSP
    ///         <c>KerbalRoster.GetUniqueKerbalName</c> generates names ~15-25
    ///         chars; 64 is generous headroom for modded long names).</item>
    ///   <item>Reject path separators <c>/</c> and <c>\</c>.</item>
    ///   <item>Reject the Windows drive-separator <c>:</c>.</item>
    ///   <item>Reject control characters (0x00 - 0x1F) including NUL.</item>
    ///   <item>Reject the reserved names <c>.</c> and <c>..</c>.</item>
    ///   <item>Reject names where <see cref="System.IO.Path.IsPathRooted"/>
    ///         returns true (defense in depth — the per-char check already
    ///         catches the typical rooted forms).</item>
    /// </list>
    ///
    /// <para>This validator runs at the wire-side dispatch boundary in
    /// <see cref="Server.Message.KerbalMsgReader.HandleMessage"/> (before
    /// dispatching to <see cref="KerbalSystem.HandleKerbalProto"/> /
    /// <see cref="KerbalSystem.HandleKerbalRemove"/>) AND at the operator
    /// boundary in <see cref="Server.Command.Command.SetVesselAgencyCommand.ExtractCrewFromVessel"/>
    /// (rejecting any extracted name that would feed an unsafe path through
    /// to the migration loop). Pure static helper for direct ServerTest
    /// coverage of every branch.</para>
    /// </summary>
    public static class KerbalNameValidator
    {
        /// <summary>
        /// Maximum permitted kerbal-name length in characters. KSP-stock names
        /// run to ~25 chars; 64 gives generous headroom for modded sources.
        /// Promoted to <c>public const</c> so tests can pin the threshold.
        /// </summary>
        public const int MaxLength = 64;

        /// <summary>
        /// Returns true when <paramref name="name"/> is safe to use as a
        /// filename component in per-agency kerbal-file paths. On false,
        /// <paramref name="reason"/> contains a short operator-visible
        /// diagnostic suitable for inclusion in a Warning log line. The
        /// reason intentionally never echoes the full input — at most a
        /// short prefix or index — so a malicious massive-length name does
        /// not produce a recursive log-amplification DoS.
        /// </summary>
        public static bool IsValid(string name, out string reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                reason = "name is empty or whitespace-only";
                return false;
            }

            if (name.Length > MaxLength)
            {
                reason = $"length {name.Length} exceeds cap {MaxLength}";
                return false;
            }

            if (name == "." || name == "..")
            {
                reason = $"reserved name '{name}' (path-walk segment)";
                return false;
            }

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c == '/' || c == '\\')
                {
                    reason = $"path-separator '{c}' at index {i}";
                    return false;
                }
                if (c == ':')
                {
                    reason = $"drive-separator ':' at index {i}";
                    return false;
                }
                if (c < 0x20)
                {
                    reason = $"control char 0x{(int)c:X2} at index {i}";
                    return false;
                }
            }

            // Defense in depth — the per-char check already catches the rooted
            // forms we know about (C:\, /...), but Path.IsPathRooted handles
            // platform-specific quirks (UNC paths, drive-relative paths on
            // Windows) so we layer it. Cost is one extra string scan; runs
            // only when the per-char check has already passed.
            if (Path.IsPathRooted(name))
            {
                reason = "name resolves as a rooted path (Path.IsPathRooted=true)";
                return false;
            }

            return true;
        }
    }
}
