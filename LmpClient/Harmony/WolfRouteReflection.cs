using LmpCommon.Message.Data.Agency;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice C] Shared reflection cache + entry-building helper used
    /// by the three WOLF route Harmony postfixes
    /// (<see cref="ScenarioPersister_CreateRoutePostfix"/>,
    /// <see cref="Route_AddResourcePostfix"/>,
    /// <see cref="Route_RemoveResourcePostfix"/>). Mirrors the
    /// <see cref="WolfDepotReflection"/> Slice B-2 precedent: one cache + one-
    /// shot resolve gate produces a single <c>[fix:WOLF-R4]</c> warning on
    /// WOLF-version-mismatch instead of three; one entry-builder means uniform
    /// behaviour across the postfix triple.
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6): a future WOLF rename
    /// or signature change is detected at first resolve; patches become no-ops
    /// for the session. Graceful degradation matches the MKS-R0 / R1 / R2 +
    /// Slice B-2 precedents in this directory. <c>WOLF.Route</c> exposes
    /// <c>OriginBody</c> / <c>OriginBiome</c> / <c>DestinationBody</c> /
    /// <c>DestinationBiome</c> / <c>Payload</c> as public auto-properties
    /// with <c>protected set</c> per Route.cs:41-47 (still readable via
    /// <see cref="PropertyInfo.GetValue"/>) + <c>GetResources()</c> as a
    /// public method returning <c>Dictionary&lt;string, int&gt;</c> per
    /// Route.cs:124-128.</para>
    ///
    /// <para><b>Threading.</b> All three postfixes fire on Unity's main thread
    /// (CreateRoute from WOLF UI route-establish click; AddResource +
    /// RemoveResource from operator allocate/unallocate actions on the route
    /// editor). Operator-driven cadence (low-frequency UI clicks, not the
    /// 50 Hz Negotiate hot path that motivated Slice B-3's WolfDepotDebouncer)
    /// — no debounce needed. The static fields here are written once at first
    /// resolve and read-only thereafter — no contention.</para>
    /// </summary>
    public static class WolfRouteReflection
    {
        // Cached reflection handles for WOLF.Route.
        private static PropertyInfo _routeOriginBody;
        private static PropertyInfo _routeOriginBiome;
        private static PropertyInfo _routeDestinationBody;
        private static PropertyInfo _routeDestinationBiome;
        private static PropertyInfo _routePayload;
        private static MethodInfo _routeGetResources;

        private static bool _routeResolved;
        private static bool _routeResolveFailed;

        // Once-only warning gate so a runtime extraction failure doesn't
        // flood KSP.log if a stale postfix keeps firing.
        private static bool _runtimeFailureLogged;

        /// <summary>
        /// Resolves the <see cref="WOLF.Route"/> property accessors lazily on
        /// first invocation. Returns false (with one-shot Warning log) when
        /// WOLF source has drifted — patches become silent no-ops for the
        /// session.
        /// </summary>
        public static bool TryResolveRouteAccessors(Type routeType)
        {
            if (_routeResolved) return true;
            if (_routeResolveFailed) return false;
            if (routeType == null) { _routeResolveFailed = true; return false; }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                _routeOriginBody = routeType.GetProperty("OriginBody", flags) ?? throw new InvalidOperationException("Route.OriginBody property not found");
                _routeOriginBiome = routeType.GetProperty("OriginBiome", flags) ?? throw new InvalidOperationException("Route.OriginBiome property not found");
                _routeDestinationBody = routeType.GetProperty("DestinationBody", flags) ?? throw new InvalidOperationException("Route.DestinationBody property not found");
                _routeDestinationBiome = routeType.GetProperty("DestinationBiome", flags) ?? throw new InvalidOperationException("Route.DestinationBiome property not found");
                _routePayload = routeType.GetProperty("Payload", flags) ?? throw new InvalidOperationException("Route.Payload property not found");
                _routeGetResources = routeType.GetMethod("GetResources", flags, binder: null, types: Type.EmptyTypes, modifiers: null)
                    ?? throw new InvalidOperationException("Route.GetResources() method not found");

                _routeResolved = true;
                return true;
            }
            catch (Exception e)
            {
                _routeResolveFailed = true;
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] WOLF.Route reflection resolve failed: {e.Message}. Per-agency WOLF route routing DISABLED for this session — WOLF version mismatch?");
                return false;
            }
        }

        /// <summary>
        /// Builds an <see cref="AgencyWolfRouteEntry"/> from a runtime
        /// WOLF.Route instance. Returns null on a resolution / extraction
        /// failure (one-shot Warning logged).
        /// </summary>
        public static AgencyWolfRouteEntry BuildEntryFromRoute(object route)
        {
            if (route == null) return null;
            if (!TryResolveRouteAccessors(route.GetType())) return null;

            try
            {
                var entry = new AgencyWolfRouteEntry
                {
                    OriginBody = (string)_routeOriginBody.GetValue(route) ?? string.Empty,
                    OriginBiome = (string)_routeOriginBiome.GetValue(route) ?? string.Empty,
                    DestinationBody = (string)_routeDestinationBody.GetValue(route) ?? string.Empty,
                    DestinationBiome = (string)_routeDestinationBiome.GetValue(route) ?? string.Empty,
                    Payload = (int)_routePayload.GetValue(route),
                    Resources = ExtractResources(route),
                };
                return entry;
            }
            catch (Exception ex)
            {
                if (!_runtimeFailureLogged)
                {
                    _runtimeFailureLogged = true;
                    LunaLog.LogError($"[LMP]: [fix:WOLF-R4] WOLF.Route field extraction failed (once-only log): {ex.GetType().Name}: {ex.Message}. Per-agency WOLF route sync is now DROPPING entries silently until KSP is restarted.");
                }
                return null;
            }
        }

        /// <summary>
        /// Reads <c>Route.GetResources()</c>'s returned Dictionary&lt;string,
        /// int&gt; per Route.cs:124-128 and converts to the wire shape. The
        /// dict is a defensive copy returned by WOLF itself (Route.cs:127:
        /// "Return a copy to insure a single source of truth"), so iteration
        /// here is safe even if the underlying _resources mutates.
        /// </summary>
        private static List<AgencyWolfRouteResourceEntry> ExtractResources(object route)
        {
            var resources = new List<AgencyWolfRouteResourceEntry>();
            var rawDict = _routeGetResources.Invoke(route, parameters: null);
            var dictionary = rawDict as IDictionary;
            if (dictionary == null) return resources;

            foreach (DictionaryEntry entry in dictionary)
            {
                try
                {
                    var name = entry.Key as string;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    var quantity = (int)entry.Value;
                    resources.Add(new AgencyWolfRouteResourceEntry
                    {
                        ResourceName = name,
                        Quantity = quantity,
                    });
                }
                catch
                {
                    // Skip the bad entry; siblings continue. Quiet — the
                    // route-level one-shot warning already fires on
                    // resolution failure.
                }
            }

            return resources;
        }
    }
}
