using LmpCommon.Message.Data.Agency;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice E] Shared reflection cache + entry-building helper used
    /// by the four WOLF crew-route Harmony postfixes
    /// (<see cref="ScenarioPersister_CreateCrewRoutePostfix"/>,
    /// <see cref="CrewRoute_EmbarkPostfix"/>,
    /// <see cref="CrewRoute_DisembarkPostfix"/>,
    /// <see cref="CrewRoute_LaunchPostfix"/>). Mirrors the
    /// <see cref="WolfRouteReflection"/> Slice C precedent: one cache + one-
    /// shot resolve gate produces a single <c>[fix:WOLF-R4]</c> Warning on
    /// WOLF-version-mismatch instead of four; one entry-builder means
    /// uniform behaviour across the postfix quartet.
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6): a future WOLF
    /// rename or signature change is detected at first resolve; patches
    /// become no-ops for the session. Graceful degradation matches the
    /// MKS-R0 / R1 / R2 + Slice B-2 / C / D precedents. <c>WOLF.CrewRoute</c>
    /// exposes <c>UniqueId</c> / <c>OriginBody</c> / <c>OriginBiome</c> /
    /// <c>DestinationBody</c> / <c>DestinationBiome</c> / <c>FlightNumber</c>
    /// / <c>ArrivalTime</c> / <c>Duration</c> / <c>EconomyBerths</c> /
    /// <c>LuxuryBerths</c> as public auto-properties with
    /// <c>protected set</c> per <c>CrewRoute.cs:51-66</c> (still readable
    /// via <see cref="PropertyInfo.GetValue"/>) + <c>FlightStatus</c> as
    /// a public auto-property of the <c>WOLFUI.FlightStatus</c> enum +
    /// <c>Passengers</c> as a public <c>List&lt;IPassenger&gt;</c> at
    /// <c>CrewRoute.cs:63-64</c>. <c>WOLF.IPassenger</c> exposes
    /// <c>Name</c> / <c>DisplayName</c> / <c>IsTourist</c> /
    /// <c>Occupation</c> / <c>Stars</c> per <c>Passenger.cs:22-26</c>.</para>
    ///
    /// <para><b>Threading.</b> All four postfixes fire on Unity's main
    /// thread (CreateCrewRoute from WOLF UI route-establish click;
    /// Embark/Disembark from operator passenger-assign clicks; Launch from
    /// WOLF_CrewTransferScenario.Launch which is also UI-driven).
    /// Operator-driven cadence (low-frequency clicks, not a per-tick
    /// stream) — no debounce needed. The static fields here are written
    /// once at first resolve and read-only thereafter — no contention.</para>
    /// </summary>
    public static class WolfCrewRouteReflection
    {
        // Cached reflection handles for WOLF.CrewRoute.
        private static PropertyInfo _crUniqueId;
        private static PropertyInfo _crOriginBody;
        private static PropertyInfo _crOriginBiome;
        private static PropertyInfo _crDestinationBody;
        private static PropertyInfo _crDestinationBiome;
        private static PropertyInfo _crFlightNumber;
        private static PropertyInfo _crFlightStatus;
        private static PropertyInfo _crArrivalTime;
        private static PropertyInfo _crDuration;
        private static PropertyInfo _crEconomyBerths;
        private static PropertyInfo _crLuxuryBerths;
        private static PropertyInfo _crPassengers;

        // Cached reflection handles for WOLF.IPassenger.
        private static PropertyInfo _pName;
        private static PropertyInfo _pDisplayName;
        private static PropertyInfo _pIsTourist;
        private static PropertyInfo _pOccupation;
        private static PropertyInfo _pStars;

        private static bool _resolved;
        private static bool _resolveFailed;

        // Once-only warning gate so a runtime extraction failure doesn't
        // flood KSP.log if a stale postfix keeps firing.
        private static bool _runtimeFailureLogged;

        /// <summary>
        /// Resolves the <see cref="WOLF.CrewRoute"/> and
        /// <see cref="WOLF.IPassenger"/> property accessors lazily on first
        /// invocation. Returns false (with one-shot Warning log) when WOLF
        /// source has drifted — patches become silent no-ops for the
        /// session.
        /// </summary>
        public static bool TryResolveAccessors(Type crewRouteType)
        {
            if (_resolved) return true;
            if (_resolveFailed) return false;
            if (crewRouteType == null) { _resolveFailed = true; return false; }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                _crUniqueId = crewRouteType.GetProperty("UniqueId", flags) ?? throw new InvalidOperationException("CrewRoute.UniqueId property not found");
                _crOriginBody = crewRouteType.GetProperty("OriginBody", flags) ?? throw new InvalidOperationException("CrewRoute.OriginBody property not found");
                _crOriginBiome = crewRouteType.GetProperty("OriginBiome", flags) ?? throw new InvalidOperationException("CrewRoute.OriginBiome property not found");
                _crDestinationBody = crewRouteType.GetProperty("DestinationBody", flags) ?? throw new InvalidOperationException("CrewRoute.DestinationBody property not found");
                _crDestinationBiome = crewRouteType.GetProperty("DestinationBiome", flags) ?? throw new InvalidOperationException("CrewRoute.DestinationBiome property not found");
                _crFlightNumber = crewRouteType.GetProperty("FlightNumber", flags) ?? throw new InvalidOperationException("CrewRoute.FlightNumber property not found");
                _crFlightStatus = crewRouteType.GetProperty("FlightStatus", flags) ?? throw new InvalidOperationException("CrewRoute.FlightStatus property not found");
                _crArrivalTime = crewRouteType.GetProperty("ArrivalTime", flags) ?? throw new InvalidOperationException("CrewRoute.ArrivalTime property not found");
                _crDuration = crewRouteType.GetProperty("Duration", flags) ?? throw new InvalidOperationException("CrewRoute.Duration property not found");
                _crEconomyBerths = crewRouteType.GetProperty("EconomyBerths", flags) ?? throw new InvalidOperationException("CrewRoute.EconomyBerths property not found");
                _crLuxuryBerths = crewRouteType.GetProperty("LuxuryBerths", flags) ?? throw new InvalidOperationException("CrewRoute.LuxuryBerths property not found");
                _crPassengers = crewRouteType.GetProperty("Passengers", flags) ?? throw new InvalidOperationException("CrewRoute.Passengers property not found");

                // IPassenger may be implemented by multiple concrete classes (Passenger
                // is the only one in current WOLF source, but reflection through the
                // interface keeps us forward-compat). Resolve via the interface so
                // we don't depend on the concrete Passenger type lookup.
                var passengerInterface = HarmonyLib.AccessTools.TypeByName("WOLF.IPassenger");
                if (passengerInterface == null)
                    throw new InvalidOperationException("WOLF.IPassenger interface not found");

                _pName = passengerInterface.GetProperty("Name", flags) ?? throw new InvalidOperationException("IPassenger.Name property not found");
                _pDisplayName = passengerInterface.GetProperty("DisplayName", flags) ?? throw new InvalidOperationException("IPassenger.DisplayName property not found");
                _pIsTourist = passengerInterface.GetProperty("IsTourist", flags) ?? throw new InvalidOperationException("IPassenger.IsTourist property not found");
                _pOccupation = passengerInterface.GetProperty("Occupation", flags) ?? throw new InvalidOperationException("IPassenger.Occupation property not found");
                _pStars = passengerInterface.GetProperty("Stars", flags) ?? throw new InvalidOperationException("IPassenger.Stars property not found");

                _resolved = true;
                return true;
            }
            catch (Exception e)
            {
                _resolveFailed = true;
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] WOLF.CrewRoute / IPassenger reflection resolve failed: {e.Message}. Per-agency WOLF crew-route routing DISABLED for this session — WOLF version mismatch?");
                return false;
            }
        }

        /// <summary>
        /// Builds an <see cref="AgencyWolfCrewRouteEntry"/> from a runtime
        /// WOLF.CrewRoute instance. Returns null on a resolution /
        /// extraction failure (one-shot Warning logged).
        /// </summary>
        public static AgencyWolfCrewRouteEntry BuildEntryFromCrewRoute(object crewRoute)
        {
            if (crewRoute == null) return null;
            if (!TryResolveAccessors(crewRoute.GetType())) return null;

            try
            {
                var entry = new AgencyWolfCrewRouteEntry
                {
                    UniqueId = (string)_crUniqueId.GetValue(crewRoute) ?? string.Empty,
                    OriginBody = (string)_crOriginBody.GetValue(crewRoute) ?? string.Empty,
                    OriginBiome = (string)_crOriginBiome.GetValue(crewRoute) ?? string.Empty,
                    DestinationBody = (string)_crDestinationBody.GetValue(crewRoute) ?? string.Empty,
                    DestinationBiome = (string)_crDestinationBiome.GetValue(crewRoute) ?? string.Empty,
                    FlightNumber = (string)_crFlightNumber.GetValue(crewRoute) ?? string.Empty,
                    // FlightStatus is the WOLFUI.FlightStatus enum; stringify
                    // to its name so the wire / disk shape matches WOLF's
                    // CrewRoute.OnSave at CrewRoute.cs:262 (which writes
                    // routeNode.AddValue(nameof(FlightStatus), FlightStatus)
                    // — KSP's ConfigNode stringifies enums by name).
                    FlightStatus = _crFlightStatus.GetValue(crewRoute)?.ToString() ?? string.Empty,
                    ArrivalTime = Convert.ToDouble(_crArrivalTime.GetValue(crewRoute)),
                    Duration = Convert.ToDouble(_crDuration.GetValue(crewRoute)),
                    EconomyBerths = (int)_crEconomyBerths.GetValue(crewRoute),
                    LuxuryBerths = (int)_crLuxuryBerths.GetValue(crewRoute),
                    Passengers = ExtractPassengers(crewRoute),
                };
                return entry;
            }
            catch (Exception ex)
            {
                if (!_runtimeFailureLogged)
                {
                    _runtimeFailureLogged = true;
                    LunaLog.LogError($"[LMP]: [fix:WOLF-R4] WOLF.CrewRoute field extraction failed (once-only log): {ex.GetType().Name}: {ex.Message}. Per-agency WOLF crew-route sync is now DROPPING entries silently until KSP is restarted.");
                }
                return null;
            }
        }

        /// <summary>
        /// Reads <c>CrewRoute.FlightStatus</c> without going through
        /// <see cref="BuildEntryFromCrewRoute"/> — used by the
        /// <see cref="CrewRoute_CheckArrivedPostfix"/> __state pattern to
        /// detect the Enroute→Arrived transition cheaply (no full entry
        /// build per WOLF UI tick). Returns null on resolution failure;
        /// the caller falls back to no-op behaviour.
        ///
        /// <para><b>Why a dedicated reader.</b> <c>CheckArrived</c> fires
        /// on every WOLF UI tick for every Enroute route. A full
        /// <c>BuildEntryFromCrewRoute</c> per call would build a wire
        /// entry just to read one field. This helper reads ONLY the
        /// FlightStatus property and stringifies the enum value — same
        /// shape as the entry-builder's FlightStatus line but standalone.</para>
        /// </summary>
        public static string ReadFlightStatus(object crewRoute)
        {
            if (crewRoute == null) return null;
            if (!TryResolveAccessors(crewRoute.GetType())) return null;
            try
            {
                return _crFlightStatus.GetValue(crewRoute)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads <c>CrewRoute.Passengers</c>'s
        /// <c>List&lt;IPassenger&gt;</c> per CrewRoute.cs:63-64 and converts
        /// to the wire shape. Defensive: empty list returns an empty wire
        /// list; per-entry exception isolation keeps siblings alive.
        /// </summary>
        private static List<AgencyWolfPassengerEntry> ExtractPassengers(object crewRoute)
        {
            var passengers = new List<AgencyWolfPassengerEntry>();
            var raw = _crPassengers.GetValue(crewRoute);
            var enumerable = raw as IEnumerable;
            if (enumerable == null) return passengers;

            foreach (var p in enumerable)
            {
                if (p == null) continue;
                try
                {
                    passengers.Add(new AgencyWolfPassengerEntry
                    {
                        Name = (string)_pName.GetValue(p) ?? string.Empty,
                        DisplayName = (string)_pDisplayName.GetValue(p) ?? string.Empty,
                        IsTourist = (bool)_pIsTourist.GetValue(p),
                        Occupation = (string)_pOccupation.GetValue(p) ?? string.Empty,
                        Stars = (int)_pStars.GetValue(p),
                    });
                }
                catch
                {
                    // Skip the bad passenger; siblings continue. Quiet — the
                    // crew-route-level one-shot warning already fires on
                    // resolution failure.
                }
            }

            return passengers;
        }
    }
}
