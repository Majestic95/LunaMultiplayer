using LmpCommon.Xml;
using System;

namespace Server.Settings.Definition
{
    /// <summary>
    /// Server-side-offload performance knobs (workstream: feature/server-relay-filtering;
    /// spec: docs/research/11-server-side-offload-spec.md). Each setting is independently
    /// gateable so an operator who suspects a regression can disable one without restarting
    /// or downgrading the binary.
    ///
    /// Lives in its own settings group (not folded into <see cref="GameplaySettingsDefinition"/>)
    /// to keep these knobs OFF the difficulty-preset reflection scan at
    /// <c>SettingsHandler.HasDifferencesAgainstGivenSetting</c> — flipping one wouldn't
    /// otherwise silently flip <c>GeneralSettings.GameDifficulty</c> to Custom.
    /// Same precedent as <see cref="WebsiteSettingsDefinition"/> and the per-agency
    /// fields living on GameplaySettings (orthogonal-to-difficulty per spec §10 caveat).
    /// </summary>
    [Serializable]
    public class OptimizationSettingsDefinition
    {
        [XmlComment(Value = "Phase 1 of server-side-offload. When true, the server drops continuous vessel-state relays " +
                            "(Position / Flightstate / Update / Resource / PartSync* / ActionGroup / Fairing) to clients NOT " +
                            "currently in Flight or TrackingStation scenes. Reduces incoming network volume by 20-40% in mixed " +
                            "cohorts (some players in editor / main menu / KSC while others fly). Catch-up + structural messages " +
                            "(Proto / Sync / Couple / Remove / Decouple / Undock) are NEVER filtered — they're needed to " +
                            "populate FlightGlobals.Vessels before scene entry. Set false if you suspect a regression — " +
                            "restores pre-Phase-1 baseline broadcast behavior. Pre-feature clients (those not shipping the " +
                            "new Scene tail-byte on PlayerStatus) are always relayed to regardless of this gate (Scene=Unknown " +
                            "= compat passthrough). Default: true. " +
                            "Up to 1s of stale filtering after a recipient changes scene (driven by the 1000ms " +
                            "CheckPlayerStatus polling cadence on the client side).")]
        public bool SceneAwareRelayEnabled { get; set; } = true;

        [XmlComment(Value = "Phase 2 of server-side-offload. When true, the server drops continuous vessel-state relays " +
                            "for vessels orbiting a DIFFERENT celestial body than the recipient's currently active vessel. " +
                            "A player flying around Kerbin no longer receives Position updates for vessels at Jool/Duna/etc. " +
                            "Reduces incoming network volume by 50-70% on interplanetary servers. Conservative same-body-only " +
                            "filter — does NOT cull within-SOI vessels (a Munar craft IS dropped when the recipient is at Kerbin " +
                            "because Mun ≠ Kerbin as exact body names, even though Mun is in Kerbin's SOI). This trade-off " +
                            "avoids the brittleness of hardcoding KSP's CelestialBody graph (which breaks under Real Solar " +
                            "System / Outer Planets Mod / GPP / etc.). Permissive when the sender's body or recipient's " +
                            "active-vessel body is unknown. Set false to revert to scene-only Phase 1 behavior. Composes with " +
                            "SceneAwareRelayEnabled (both filters apply when both true). Default: true.")]
        public bool SameBodyFilterEnabled { get; set; } = true;
    }
}
