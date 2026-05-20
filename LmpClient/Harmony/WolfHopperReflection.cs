using LmpCommon.Message.Data.Agency;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 4 Slice D] Shared reflection cache + entry-building helper used
    /// by <see cref="ScenarioPersister_CreateHopperPostfix"/> and any future
    /// hopper-mutation postfix. Mirrors the
    /// <see cref="WolfRouteReflection"/> Slice C precedent: one cache + one-
    /// shot resolve gate produces a single <c>[fix:WOLF-R4]</c> warning on
    /// WOLF-version-mismatch instead of N.
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6): a future WOLF rename
    /// or signature change is detected at first resolve; patches become no-ops
    /// for the session. Graceful degradation matches the MKS-R0 / R1 / R2 +
    /// Slice B-2 / Slice C precedents in this directory.</para>
    ///
    /// <para><b>Source shape</b> at MKS SHA <c>ed0f6aa6</c>:
    /// <c>WOLF.HopperMetadata</c> at
    /// <c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\HopperMetadata.cs</c>
    /// exposes <c>Id</c> (string) + <c>Depot</c> (IDepot — read
    /// <c>.Body</c> + <c>.Biome</c>) + <c>Recipe</c> (IRecipe — read
    /// <c>.InputIngredients</c> which is <c>Dictionary&lt;string,int&gt;</c>).
    /// The wire format flattens the recipe to a comma-joined
    /// <c>"resource,qty,resource,qty,..."</c> string mirroring WOLF's own
    /// persistence at <c>HopperMetadata.cs:44-48</c> — server-side router
    /// stores the string as-is; the projector emits it back as-is, so the
    /// scenario blob round-trips byte-identical with what WOLF would have
    /// emitted itself.</para>
    ///
    /// <para><b>Threading.</b> Create / Remove postfixes fire on Unity's main
    /// thread (WOLF UI recipe-pick + decommission clicks). Operator-driven
    /// cadence — no debounce needed (unlike Slice B-3 Negotiate). The static
    /// fields here are written once at first resolve and read-only thereafter
    /// — no contention.</para>
    /// </summary>
    public static class WolfHopperReflection
    {
        // Depot accessors (the CreateHopper postfix receives IDepot directly
        // as a Harmony-bound parameter, so we don't need to reach through a
        // HopperMetadata.Depot property).
        private static PropertyInfo _depotBody;
        private static PropertyInfo _depotBiome;

        // Recipe accessors.
        private static PropertyInfo _recipeInputIngredients;

        private static bool _hopperResolved;
        private static bool _hopperResolveFailed;

        // Once-only warning gate so a runtime extraction failure doesn't
        // flood KSP.log if a stale postfix keeps firing.
        private static bool _runtimeFailureLogged;

        /// <summary>
        /// Builds an <see cref="AgencyWolfHopperEntry"/> from the components
        /// available at the <c>ScenarioPersister.CreateHopper</c> postfix
        /// (per Harmony parameter-name binding): the just-minted Id, the
        /// IDepot argument, and the IRecipe argument. Returns null on a
        /// resolution / extraction failure (one-shot Warning logged).
        ///
        /// <para><b>Why Components and not a Hopper instance?</b>
        /// <c>CreateHopper(IDepot, IRecipe)</c> at
        /// <c>ScenarioPersister.cs:95-101</c> returns <c>string</c> (the Id),
        /// not the freshly-constructed HopperMetadata. Walking the persister's
        /// private <c>Hoppers</c> list to find the one with matching Id would
        /// require another reflection hop into ScenarioPersister's internals.
        /// Components keeps the postfix shallow + reads naturally — the
        /// IDepot and IRecipe are already on the original method's
        /// parameter list.</para>
        ///
        /// <para><b>Recipe encoding.</b> The flat
        /// <c>"resource,qty,resource,qty,..."</c> string mirrors WOLF's own
        /// on-disk format at <c>HopperMetadata.cs:44-48</c>. Null/empty
        /// input dict → empty string.</para>
        /// </summary>
        public static AgencyWolfHopperEntry BuildEntryFromComponents(string id, object depot, object recipe)
        {
            if (depot == null) return null;
            if (!TryResolveDepotAndRecipeAccessors(depot.GetType(), recipe?.GetType())) return null;

            try
            {
                return new AgencyWolfHopperEntry
                {
                    Id = id ?? string.Empty,
                    Body = (string)_depotBody.GetValue(depot) ?? string.Empty,
                    Biome = (string)_depotBiome.GetValue(depot) ?? string.Empty,
                    Recipe = ExtractRecipe(recipe),
                };
            }
            catch (Exception ex)
            {
                if (!_runtimeFailureLogged)
                {
                    _runtimeFailureLogged = true;
                    LunaLog.LogError($"[LMP]: [fix:WOLF-R4] WOLF Hopper component extraction failed (once-only log): {ex.GetType().Name}: {ex.Message}. Per-agency WOLF hopper sync is now DROPPING entries silently until KSP is restarted.");
                }
                return null;
            }
        }

        /// <summary>
        /// Components-path resolver. Distinct from
        /// <see cref="TryResolveHopperAccessors"/> because the components
        /// path doesn't have a HopperMetadata instance — it gets IDepot +
        /// IRecipe directly from the postfix's Harmony-bound parameters.
        /// One-shot Warning + per-session disable matches the full-instance
        /// path.
        /// </summary>
        private static bool TryResolveDepotAndRecipeAccessors(Type depotType, Type recipeType)
        {
            if (_hopperResolved) return true;
            if (_hopperResolveFailed) return false;
            if (depotType == null) { _hopperResolveFailed = true; return false; }
            // recipeType may legitimately be null if the postfix is called
            // with a null IRecipe — in that case ExtractRecipe returns
            // empty string. We still need depot accessors.

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                _depotBody = depotType.GetProperty("Body", flags) ?? throw new InvalidOperationException("IDepot.Body property not found");
                _depotBiome = depotType.GetProperty("Biome", flags) ?? throw new InvalidOperationException("IDepot.Biome property not found");

                if (recipeType != null)
                {
                    _recipeInputIngredients = recipeType.GetProperty("InputIngredients", flags) ?? throw new InvalidOperationException("IRecipe.InputIngredients property not found");
                }

                _hopperResolved = true;
                return true;
            }
            catch (Exception e)
            {
                _hopperResolveFailed = true;
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] WOLF Hopper depot/recipe reflection resolve failed: {e.Message}. Per-agency WOLF hopper routing DISABLED for this session — WOLF version mismatch?");
                return false;
            }
        }

        /// <summary>
        /// Reads an IRecipe's <c>InputIngredients</c> and serializes to the
        /// same flat comma-joined format WOLF persists at
        /// <c>HopperMetadata.cs:44-48</c>. Output dictionary is ignored
        /// (WOLF always stores it as empty per <c>HopperMetadata.OnLoad</c>
        /// at line 34).
        /// </summary>
        private static string ExtractRecipe(object recipe)
        {
            if (recipe == null || _recipeInputIngredients == null) return string.Empty;

            var rawDict = _recipeInputIngredients.GetValue(recipe);
            var dictionary = rawDict as IDictionary;
            if (dictionary == null || dictionary.Count == 0) return string.Empty;

            // StringBuilder + comma-join mirroring WOLF's
            // string.Join(",", ingredients.ToArray()) at HopperMetadata.cs:46.
            var sb = new StringBuilder();
            var first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                try
                {
                    var name = entry.Key as string;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    var quantity = (int)entry.Value;
                    if (!first) sb.Append(',');
                    sb.Append(name);
                    sb.Append(',');
                    sb.Append(quantity);
                    first = false;
                }
                catch
                {
                    // Skip the bad entry; siblings continue. Quiet — the
                    // hopper-level one-shot warning already fires on
                    // resolution failure.
                }
            }

            return sb.ToString();
        }
    }
}
