using Server.Log;
using System.Collections.Generic;

namespace Server.System.Vessel
{
    /// <summary>
    /// Server-side defensive normalisation of incoming vessel ConfigNodes. Runs
    /// inside <see cref="VesselDataUpdater.RawConfigNodeInsertOrUpdate"/> on
    /// every proto-vessel write so the universe and downstream clients only
    /// ever see canonical English field values.
    ///
    /// See <c>docs/research/01-bug-inventory.md</c> § BUG-013. KSP localises a
    /// handful of <c>stateString</c> fields into the player's UI language at
    /// serialisation time; a non-English client uploads a vessel whose reaction
    /// wheel <c>stateString</c> is e.g. <c>Работает</c>; downstream English
    /// clients try to <c>Enum.Parse</c> it and NRE in
    /// <c>VesselPrecalculate.MainPhysics</c> for every Update tick. One bad
    /// vessel can take the whole server unplayable.
    /// </summary>
    public static class VesselSanitizer
    {
        /// <summary>
        /// Canonical English values for <c>ModuleReactionWheel.stateString</c>
        /// as KSP actually serialises them. <c>stateString</c> is the localised
        /// display name of the <c>WheelState</c> enum (Active / Disabled /
        /// Broken). In English, the Active state is rendered as <c>"Running"</c>
        /// — see ServerTest/XmlExampleFiles for real-world vessel ConfigNodes.
        /// Anything outside this set was localised away from English and
        /// becomes the canonical replacement <see cref="ReactionWheelDefault"/>.
        /// </summary>
        private static readonly HashSet<string> ReactionWheelStates = new HashSet<string>
        {
            "Running",      // WheelState.Active (default operating state)
            "Disabled",     // WheelState.Disabled
            "Broken",       // WheelState.Broken
        };

        /// <summary>
        /// Safe-default replacement when a stateString is non-canonical. We
        /// pick the active-state string because the alternative (Disabled)
        /// would silently turn off the player's reaction wheels — a
        /// regression even on a non-bugged client.
        /// </summary>
        private const string ReactionWheelDefault = "Running";

        // KSP ships ModuleReactionWheel and a number of mod-side V2 / variant
        // modules that share the same WheelState enum + stateString field.
        // Whitelist by exact module name so we don't accidentally clobber an
        // unrelated module's stateString.
        private static readonly HashSet<string> ReactionWheelModuleNames = new HashSet<string>
        {
            "ModuleReactionWheel",
            "ModuleReactionWheelV2",
        };

        /// <summary>
        /// Walks the vessel's parts and rewrites any non-canonical
        /// <c>stateString</c> on a reaction-wheel module back to <c>"Active"</c>.
        /// Returns the number of fields rewritten — call sites log once per
        /// vessel rather than per part. Idempotent: a vessel that's already
        /// clean returns 0 and is not touched.
        /// </summary>
        public static int SanitizeReactionWheelStateStrings(Classes.Vessel vessel)
        {
            if (vessel == null) return 0;
            var rewritten = 0;

            foreach (var part in vessel.Parts.GetAllValues())
            {
                foreach (var moduleEntry in part.Modules.GetAll())
                {
                    if (!ReactionWheelModuleNames.Contains(moduleEntry.Key)) continue;

                    var current = moduleEntry.Value.GetValue("stateString")?.Value;
                    if (string.IsNullOrEmpty(current)) continue;
                    if (ReactionWheelStates.Contains(current)) continue;

                    moduleEntry.Value.UpdateValue("stateString", ReactionWheelDefault);
                    rewritten++;
                }
            }

            return rewritten;
        }

        /// <summary>
        /// Runs every sanitiser on the vessel in order. New per-module fixes
        /// (BUG-013 family) plug in here so callers stay one-line.
        /// </summary>
        public static void Sanitize(Classes.Vessel vessel, string vesselIdForLog)
        {
            var rewrites = SanitizeReactionWheelStateStrings(vessel);
            if (rewrites > 0)
                LunaLog.Normal($"[fix:BUG-013] sanitised {rewrites} non-canonical ModuleReactionWheel stateString field(s) on vessel {vesselIdForLog}");
        }
    }
}
