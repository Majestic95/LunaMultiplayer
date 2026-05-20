using LmpCommon.Message.Data.Agency;
using System;
using System.Reflection;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice D] Shared reflection cache + entry-building helper used
    /// by <see cref="ScenarioPersister_CreateTerminalPostfix"/>. Mirrors the
    /// <see cref="WolfRouteReflection"/> Slice C precedent.
    ///
    /// <para><b>Source shape</b> at MKS SHA <c>ed0f6aa6</c>:
    /// <c>WOLF.TerminalMetadata</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\TerminalMetadata.cs</c>
    /// exposes <c>Id</c> (string — Guid in <c>ToString("N")</c> form, NO
    /// hyphens — distinct from <see cref="WolfHopperReflection"/>'s
    /// with-hyphens form) + <c>Body</c> (string) + <c>Biome</c> (string).
    /// All three are <c>{ get; private set; }</c> auto-properties (readable
    /// via reflection regardless of the private setter).</para>
    ///
    /// <para><b>Threading.</b> Create / Remove postfixes fire on Unity's main
    /// thread (WOLF UI terminal-establish + decommission clicks). Operator-
    /// driven cadence — no debounce needed.</para>
    /// </summary>
    public static class WolfTerminalReflection
    {
        // Cached reflection handles for WOLF.TerminalMetadata.
        private static PropertyInfo _terminalId;
        private static PropertyInfo _terminalBody;
        private static PropertyInfo _terminalBiome;

        private static bool _terminalResolved;
        private static bool _terminalResolveFailed;

        // Once-only warning gate so a runtime extraction failure doesn't
        // flood KSP.log if a stale postfix keeps firing.
        private static bool _runtimeFailureLogged;

        /// <summary>
        /// Resolves the <c>WOLF.TerminalMetadata</c> property accessors
        /// lazily on first invocation. Returns false (with one-shot Warning
        /// log) when WOLF source has drifted — patches become silent no-ops
        /// for the session.
        /// </summary>
        public static bool TryResolveTerminalAccessors(Type terminalType)
        {
            if (_terminalResolved) return true;
            if (_terminalResolveFailed) return false;
            if (terminalType == null) { _terminalResolveFailed = true; return false; }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                _terminalId = terminalType.GetProperty("Id", flags) ?? throw new InvalidOperationException("TerminalMetadata.Id property not found");
                _terminalBody = terminalType.GetProperty("Body", flags) ?? throw new InvalidOperationException("TerminalMetadata.Body property not found");
                _terminalBiome = terminalType.GetProperty("Biome", flags) ?? throw new InvalidOperationException("TerminalMetadata.Biome property not found");

                _terminalResolved = true;
                return true;
            }
            catch (Exception e)
            {
                _terminalResolveFailed = true;
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] WOLF.TerminalMetadata reflection resolve failed: {e.Message}. Per-agency WOLF terminal routing DISABLED for this session — WOLF version mismatch?");
                return false;
            }
        }

        /// <summary>
        /// Builds an <see cref="AgencyWolfTerminalEntry"/> from a runtime
        /// <c>WOLF.TerminalMetadata</c> instance. Returns null on a
        /// resolution / extraction failure (one-shot Warning logged).
        /// </summary>
        public static AgencyWolfTerminalEntry BuildEntryFromTerminal(object terminal)
        {
            if (terminal == null) return null;
            if (!TryResolveTerminalAccessors(terminal.GetType())) return null;

            try
            {
                return new AgencyWolfTerminalEntry
                {
                    Id = (string)_terminalId.GetValue(terminal) ?? string.Empty,
                    Body = (string)_terminalBody.GetValue(terminal) ?? string.Empty,
                    Biome = (string)_terminalBiome.GetValue(terminal) ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                if (!_runtimeFailureLogged)
                {
                    _runtimeFailureLogged = true;
                    LunaLog.LogError($"[LMP]: [fix:WOLF-R4] WOLF.TerminalMetadata field extraction failed (once-only log): {ex.GetType().Name}: {ex.Message}. Per-agency WOLF terminal sync is now DROPPING entries silently until KSP is restarted.");
                }
                return null;
            }
        }
    }
}
