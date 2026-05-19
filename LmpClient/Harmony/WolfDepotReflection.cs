using LmpClient.Systems.Agency;
using LmpCommon.Message.Data.Agency;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice B-2] Shared reflection cache + entry-building helper used
    /// by the three WOLF depot Harmony postfixes
    /// (<see cref="ScenarioPersister_CreateDepotPostfix"/>,
    /// <see cref="Depot_EstablishPostfix"/>, <see cref="Depot_SurveyPostfix"/>).
    /// Mirrors the Phase 3
    /// <see cref="OrbitalLogisticsReflection"/> precedent: one cache + one-shot
    /// resolve gate produces a single <c>[fix:WOLF-R4]</c> warning on
    /// WOLF-version-mismatch instead of three; one entry-builder means uniform
    /// behaviour across the postfix triple.
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6): a future WOLF rename
    /// or signature change is detected at first resolve; patches become no-ops
    /// for the session. Graceful degradation matches the MKS-R0 / R1 / R2
    /// precedents in this directory. <c>WOLF.Depot</c> exposes <c>Body</c> /
    /// <c>Biome</c> / <c>IsEstablished</c> / <c>IsSurveyed</c> as public
    /// auto-properties + <c>GetResources()</c> as a public method returning
    /// <c>List&lt;IResourceStream&gt;</c>. <c>IResourceStream</c> exposes
    /// <c>ResourceName</c> / <c>Incoming</c> / <c>Outgoing</c>.</para>
    ///
    /// <para><b>Threading.</b> All three postfixes fire on Unity's main thread
    /// (CreateDepot from <c>WOLF_DepotModule</c> activation; Establish + Survey
    /// from in-game UI clicks). Reflection is single-threaded against the patch
    /// instances. The static fields here are written once at first resolve
    /// and read-only thereafter — no contention.</para>
    /// </summary>
    public static class WolfDepotReflection
    {
        // Cached reflection handles for WOLF.Depot.
        private static PropertyInfo _depotBody;
        private static PropertyInfo _depotBiome;
        private static PropertyInfo _depotIsEstablished;
        private static PropertyInfo _depotIsSurveyed;
        private static MethodInfo _depotGetResources;

        // Cached reflection handles for WOLF.IResourceStream (returned by
        // GetResources). Resolved lazily on first non-empty stream list since
        // the IResourceStream type is the concrete interface implementation
        // (ResourceStream class).
        private static PropertyInfo _streamResourceName;
        private static PropertyInfo _streamIncoming;
        private static PropertyInfo _streamOutgoing;

        private static bool _depotResolved;
        private static bool _depotResolveFailed;

        // Once-only warning gate so a runtime extraction failure doesn't
        // flood KSP.log on the hot-path postfix.
        private static bool _runtimeFailureLogged;

        /// <summary>
        /// Resolves the <see cref="WOLF.Depot"/> property accessors lazily on
        /// first invocation. Returns false (with one-shot Warning log) when
        /// WOLF source has drifted — patches become silent no-ops for the
        /// session.
        /// </summary>
        public static bool TryResolveDepotAccessors(Type depotType)
        {
            if (_depotResolved) return true;
            if (_depotResolveFailed) return false;
            if (depotType == null) { _depotResolveFailed = true; return false; }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                _depotBody = depotType.GetProperty("Body", flags) ?? throw new InvalidOperationException("Depot.Body property not found");
                _depotBiome = depotType.GetProperty("Biome", flags) ?? throw new InvalidOperationException("Depot.Biome property not found");
                _depotIsEstablished = depotType.GetProperty("IsEstablished", flags) ?? throw new InvalidOperationException("Depot.IsEstablished property not found");
                _depotIsSurveyed = depotType.GetProperty("IsSurveyed", flags) ?? throw new InvalidOperationException("Depot.IsSurveyed property not found");
                _depotGetResources = depotType.GetMethod("GetResources", flags, binder: null, types: Type.EmptyTypes, modifiers: null)
                    ?? throw new InvalidOperationException("Depot.GetResources() method not found");

                _depotResolved = true;
                return true;
            }
            catch (Exception e)
            {
                _depotResolveFailed = true;
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] WOLF.Depot reflection resolve failed: {e.Message}. Per-agency WOLF depot routing DISABLED for this session — WOLF version mismatch?");
                return false;
            }
        }

        /// <summary>
        /// Builds an <see cref="AgencyWolfDepotEntry"/> from a runtime
        /// WOLF.Depot instance. Returns null on a resolution / extraction
        /// failure (one-shot Warning logged).
        /// </summary>
        public static AgencyWolfDepotEntry BuildEntryFromDepot(object depot)
        {
            if (depot == null) return null;
            if (!TryResolveDepotAccessors(depot.GetType())) return null;

            try
            {
                var entry = new AgencyWolfDepotEntry
                {
                    Body = (string)_depotBody.GetValue(depot) ?? string.Empty,
                    Biome = (string)_depotBiome.GetValue(depot) ?? string.Empty,
                    IsEstablished = (bool)_depotIsEstablished.GetValue(depot),
                    IsSurveyed = (bool)_depotIsSurveyed.GetValue(depot),
                    ResourceStreams = ExtractResourceStreams(depot),
                };
                return entry;
            }
            catch (Exception ex)
            {
                if (!_runtimeFailureLogged)
                {
                    _runtimeFailureLogged = true;
                    LunaLog.LogError($"[LMP]: [fix:WOLF-R4] WOLF.Depot field extraction failed (once-only log): {ex.GetType().Name}: {ex.Message}. Per-agency WOLF depot sync is now DROPPING entries silently until KSP is restarted.");
                }
                return null;
            }
        }

        /// <summary>
        /// Iterates <c>Depot.GetResources()</c>'s returned list, building
        /// <see cref="AgencyWolfResourceStreamEntry"/> records. Resolves the
        /// IResourceStream property accessors lazily on first non-empty list.
        /// Returns an empty list on resolution failure (the depot-level
        /// failure log already fired).
        /// </summary>
        private static List<AgencyWolfResourceStreamEntry> ExtractResourceStreams(object depot)
        {
            var streams = new List<AgencyWolfResourceStreamEntry>();
            var rawList = _depotGetResources.Invoke(depot, parameters: null);
            var enumerable = rawList as IEnumerable;
            if (enumerable == null) return streams;

            foreach (var streamObj in enumerable)
            {
                if (streamObj == null) continue;

                if (_streamResourceName == null)
                {
                    // First non-null stream — resolve the IResourceStream
                    // accessors against the concrete runtime type. Cached
                    // for subsequent streams + invocations.
                    if (!TryResolveStreamAccessors(streamObj.GetType()))
                        return streams;
                }

                try
                {
                    streams.Add(new AgencyWolfResourceStreamEntry
                    {
                        ResourceName = (string)_streamResourceName.GetValue(streamObj) ?? string.Empty,
                        Incoming = (int)_streamIncoming.GetValue(streamObj),
                        Outgoing = (int)_streamOutgoing.GetValue(streamObj),
                    });
                }
                catch
                {
                    // Skip the bad entry; siblings continue. Quiet — the
                    // depot-level one-shot warning already fires on
                    // resolution failure.
                }
            }

            return streams;
        }

        private static bool TryResolveStreamAccessors(Type streamType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                _streamResourceName = streamType.GetProperty("ResourceName", flags) ?? throw new InvalidOperationException("ResourceStream.ResourceName property not found");
                _streamIncoming = streamType.GetProperty("Incoming", flags) ?? throw new InvalidOperationException("ResourceStream.Incoming property not found");
                _streamOutgoing = streamType.GetProperty("Outgoing", flags) ?? throw new InvalidOperationException("ResourceStream.Outgoing property not found");
                return true;
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] WOLF.IResourceStream reflection resolve failed: {e.Message}. Per-agency WOLF ResourceStreams sync DISABLED for this session.");
                return false;
            }
        }
    }
}
