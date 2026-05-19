using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Data.Agency;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 3 Slice C] MKS-R2 — Postfix on
    /// <c>KolonyTools.PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources(Part, string, bool)</c>.
    /// Mirrors every per-vessel planetary-logistics warehouse mutation into the
    /// per-agency wire so the server-side <c>AgencyPlanetaryRouter</c> can
    /// route + persist + echo + project the mutation out only to the owning
    /// agency.
    ///
    /// <para><b>Hook anchor + Q1/Q2 resolution.</b> Anchor is
    /// <c>ModulePlanetaryLogistics.LevelResources</c> per pre-spec §2.a anchor
    /// table (lines 97-99). The postfix sits on the PartModule mutator that
    /// (a) carries <c>this.vessel</c> directly (Q2 verified at
    /// <c>ModulePlanetaryLogistics.cs:41+81</c> — <c>vessel.FindPartModulesImplementing</c>
    /// + <c>vessel.mainBody.flightGlobalsIndex</c>), and (b) is the sole
    /// caller of <c>PlanetaryLogisticsManager.TrackLogEntry</c> across all
    /// three of MKS' mutation branches (push/pull/overflow-store at lines
    /// 91/113/133). The manager itself is NOT a candidate anchor because
    /// <c>PlanetaryLogisticsEntry</c> lacks any vessel-id field — the postfix
    /// needs the vessel to derive owning-agency for the cross-agency
    /// partition decision.</para>
    ///
    /// <para><b>Brittleness mitigation (pre-spec §6 item 2).</b>
    /// <c>LevelResources</c> is PRIVATE (<c>private void LevelResources</c> at
    /// <c>ModulePlanetaryLogistics.cs:78</c>). Harmony works on private
    /// methods but they are more brittle to signature change than public
    /// surfaces. Mitigation: imperative registration via
    /// <see cref="LmpClient.Base.HarmonyPatcher.PatchModulePlanetaryLogistics"/>
    /// uses <c>AccessTools.TypeByName</c> + <c>AccessTools.Method(...,
    /// BindingFlags.Instance | BindingFlags.NonPublic)</c> with explicit
    /// not-found warning at boot. A future MKS rename / signature change
    /// produces a single <c>[fix:MKS-R2]</c> warning line at boot and the
    /// postfix becomes a no-op for the rest of the session — graceful
    /// degradation matches the MKS-R0 + MKS-R1 + Slice B kolony self-disable
    /// pattern, single source of truth in the <c>[fix:MKS-R[012]]</c> grep
    /// namespace.</para>
    ///
    /// <para><b>Why not anchor at TrackLogEntry instead?</b> The pre-spec §11
    /// Q1 implementation-time hint suggested trying
    /// <c>PlanetaryLogisticsManager.TrackLogEntry</c> first because the
    /// public-method anchor is less brittle, but the resolved §2.a anchor map
    /// reads "<c>TrackLogEntry</c> is NOT the postfix anchor — entry lacks
    /// vessel-id; router needs caller-vessel context." A
    /// <c>TrackLogEntry</c>-postfix would need a sibling
    /// <c>LevelResources</c>-prefix to capture <c>this.vessel</c> into a
    /// thread-local that the postfix then consumes — more moving parts than
    /// the single private-method postfix.</para>
    ///
    /// <para><b>Gate.</b> Under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op (no wire emit, no state mutation suppression).
    /// The legacy 30s SHA pass on <c>PlanetaryLogisticsScenario</c> covers
    /// shared-mode propagation unchanged — strict dual-mode silence. The
    /// <c>IgnoredScenarios</c> Option B filter (Slice C) provides the
    /// counterpart broadcast suppression under gate=on so the postfix + the
    /// projector are the SOLE planetary-data path under per-agency mode.</para>
    ///
    /// <para><b>Post-mutation read.</b> The postfix doesn't observe the
    /// <c>PlanetaryLogisticsEntry</c> passed through <c>TrackLogEntry</c>
    /// directly (it's a local in <c>LevelResources</c>). Instead it reads
    /// the current entry state from
    /// <c>PlanetaryLogisticsManager.Instance.PlanetaryLogisticsInfo</c>
    /// post-call — looking up the entry matching <c>(resource, body)</c>.
    /// Three considerations:</para>
    /// <list type="bullet">
    ///   <item><b>Idempotency:</b> if <c>LevelResources</c> early-returned
    ///        without mutating (e.g. branch 1's "entry doesn't exist" guard
    ///        at MKS line 88-89), the entry may or may not exist
    ///        post-call. If it doesn't exist, the postfix is a no-op. If
    ///        it exists from a prior mutation, the postfix sends the
    ///        current value — idempotent upsert at the server, no harm.</item>
    ///   <item><b>Manager singleton access:</b> reflected via
    ///        <c>AccessTools.TypeByName("PlanetaryLogistics.PlanetaryLogisticsManager")</c>
    ///        + cached <c>PropertyInfo</c> for the static <c>Instance</c> +
    ///        the <c>PlanetaryLogisticsInfo</c> instance property. First-call
    ///        resolution caches the handles; subsequent calls reuse.</item>
    ///   <item><b>Entry lookup:</b> linear scan of
    ///        <c>PlanetaryLogisticsInfo</c> (a <c>List&lt;PlanetaryLogisticsEntry&gt;</c>)
    ///        looking for the match. Megabases with N body+resource pairs
    ///        are O(N) per postfix call; pre-spec §11 Q6 cadence soak budget
    ///        is the relevant ceiling. If telemetry shows this as a hotspot,
    ///        the follow-up is per-batch coalescing at
    ///        <see cref="AgencyPlanetarySender"/> (defer until measured).</item>
    /// </list>
    ///
    /// <para><b>One-shot error log gate.</b> A runtime exception inside the
    /// postfix's reflective lookup chain would silently drop entries forever
    /// — the operator's only signal is "my per-agency planetary state isn't
    /// syncing." A single <c>LunaLog.LogError</c> on first failure surfaces
    /// the symptom; silent-drop posture after that keeps the hot-path
    /// postfix from log-spamming on a persistent failure mode. Matches
    /// Slice B's kolony postfix.</para>
    /// </summary>
    public static class ModulePlanetaryLogistics_LevelResourcesPostfix
    {
        // Cached reflection handles for the PlanetaryLogisticsManager singleton.
        // Resolved on first invocation; subsequent calls reuse. _resolveFailed
        // makes the postfix a no-op for the rest of the session if MKS' shape
        // doesn't match the pinned SHA — operator must restart KSP after
        // updating MKS to a matching version.
        private static Type _managerType;
        private static PropertyInfo _instanceProperty;
        private static PropertyInfo _logisticsInfoProperty;
        private static FieldInfo _entryBodyIndexField;
        private static FieldInfo _entryResourceNameField;
        private static FieldInfo _entryStoredQuantityField;
        private static bool _resolved;
        private static bool _resolveFailed;

        // One-shot error log gate (matches Slice B kolony postfix). Without
        // this, a persistent runtime failure mode would flood KSP.log every
        // FixedUpdate tick of every warehouse vessel.
        private static bool _postfixRuntimeFailureLogged;

        /// <summary>
        /// Postfix entry point. Harmony binds the <c>__instance</c> /
        /// <c>resource</c> parameters positionally from
        /// <c>LevelResources(Part rPart, string resource, bool hasSkill)</c>.
        /// <c>__instance</c> is the <c>ModulePlanetaryLogistics</c> PartModule
        /// — declared as <see cref="object"/> here so this file compiles
        /// without a KolonyTools reference; cast to <see cref="PartModule"/>
        /// at use site since PartModule is a KSP-side compile-time dep.
        /// </summary>
        internal static void Postfix(object __instance, string resource)
        {
            // Gate. Cheap early-return under shared mode + Sandbox / Science.
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (__instance == null || string.IsNullOrEmpty(resource)) return;

            try
            {
                // PartModule is a KSP compile-time dep; the cast is safe
                // because the postfix is registered against the
                // ModulePlanetaryLogistics : PartModule type.
                var module = __instance as PartModule;
                if (module == null) return;

                var vessel = module.vessel;
                if (vessel == null || vessel.mainBody == null) return;
                var bodyIndex = vessel.mainBody.flightGlobalsIndex;
                var vesselId = vessel.id;
                if (vesselId == Guid.Empty) return; // mid-scene-load defensive

                if (!TryResolveReflection()) return;

                // Read the post-mutation StoredQuantity from the manager
                // singleton. May be null if LevelResources early-returned
                // without producing or updating an entry — that's a no-op
                // (nothing to send).
                if (!TryReadStoredQuantity(resource, bodyIndex, out var storedQuantity))
                    return;

                var entry = new AgencyPlanetaryEntry
                {
                    OwningVesselId = vesselId,
                    BodyIndex = bodyIndex,
                    ResourceName = resource,
                    StoredQuantity = storedQuantity,
                };

                AgencyPlanetarySender.SendMutation(entry);
            }
            catch (Exception ex)
            {
                // Per-postfix exception isolation. A single failed extraction
                // must not crash MKS' own LevelResources path (we're a postfix
                // — the original already ran successfully). Log once at
                // LunaLog.LogError so operators have a grep target if
                // per-agency planetary sync stops working; silent after the
                // first occurrence so the hot-path postfix doesn't flood
                // KSP.log on a persistent failure.
                if (!_postfixRuntimeFailureLogged)
                {
                    _postfixRuntimeFailureLogged = true;
                    LunaLog.LogError($"[LMP]: [fix:MKS-R2] ModulePlanetaryLogistics.LevelResources postfix runtime failure (once-only log): {ex.GetType().Name}: {ex.Message}. Per-agency planetary sync is now DROPPING entries silently until KSP is restarted; investigate MKS / LMP-fork version mismatch.");
                }
            }
        }

        /// <summary>
        /// First-call reflection resolution. Caches all four handles
        /// (manager type + Instance property + PlanetaryLogisticsInfo
        /// property + the three entry fields we read). Returns false if
        /// MKS' shape doesn't match the pinned SHA <c>ed0f6aa6</c>.
        /// </summary>
        private static bool TryResolveReflection()
        {
            if (_resolved) return true;
            if (_resolveFailed) return false;

            try
            {
                _managerType = HarmonyLib.AccessTools.TypeByName("PlanetaryLogistics.PlanetaryLogisticsManager");
                if (_managerType == null) { _resolveFailed = true; return false; }

                _instanceProperty = _managerType.GetProperty("Instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (_instanceProperty == null) { _resolveFailed = true; return false; }

                _logisticsInfoProperty = _managerType.GetProperty("PlanetaryLogisticsInfo",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_logisticsInfoProperty == null) { _resolveFailed = true; return false; }

                // PlanetaryLogisticsEntry has explicit `public {get; set;}`
                // auto-properties (verified at SHA ed0f6aa6
                // PlanetaryLogisticsEntry.cs:1-9). Resolve via the
                // backing-field-first lookup pattern (matches Slice B's
                // KolonizationManager_TrackLogEntryPostfix.ResolveField).
                var entryType = HarmonyLib.AccessTools.TypeByName("PlanetaryLogistics.PlanetaryLogisticsEntry");
                if (entryType == null) { _resolveFailed = true; return false; }

                _entryBodyIndexField = ResolveEntryField(entryType, "BodyIndex", typeof(int));
                _entryResourceNameField = ResolveEntryField(entryType, "ResourceName", typeof(string));
                _entryStoredQuantityField = ResolveEntryField(entryType, "StoredQuantity", typeof(double));

                _resolved = true;
                return true;
            }
            catch (Exception)
            {
                _resolveFailed = true;
                return false;
            }
        }

        private static FieldInfo ResolveEntryField(Type entryType, string memberName, Type expectedType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var backing = entryType.GetField($"<{memberName}>k__BackingField", flags);
            if (backing != null && backing.FieldType == expectedType) return backing;
            var direct = entryType.GetField(memberName, flags);
            if (direct != null && direct.FieldType == expectedType) return direct;
            throw new InvalidOperationException(
                $"[fix:MKS-R2] PlanetaryLogisticsEntry member '{memberName}' (type {expectedType.Name}) not found on {entryType.FullName}");
        }

        /// <summary>
        /// Linear scan of <c>PlanetaryLogisticsManager.Instance.PlanetaryLogisticsInfo</c>
        /// for the entry matching <paramref name="resource"/> + <paramref name="bodyIndex"/>.
        /// Returns false (with <paramref name="storedQuantity"/>=0) when no
        /// matching entry exists post-<c>LevelResources</c> — caller treats
        /// as a no-op (the early-return branches of LevelResources can leave
        /// the entry absent).
        /// </summary>
        private static bool TryReadStoredQuantity(string resource, int bodyIndex, out double storedQuantity)
        {
            storedQuantity = 0d;
            var managerInstance = _instanceProperty.GetValue(null, null);
            if (managerInstance == null) return false;
            var infoList = _logisticsInfoProperty.GetValue(managerInstance, null) as IEnumerable;
            if (infoList == null) return false;

            foreach (var entry in infoList)
            {
                if (entry == null) continue;
                var entryBody = (int)_entryBodyIndexField.GetValue(entry);
                if (entryBody != bodyIndex) continue;
                var entryResource = (string)_entryResourceNameField.GetValue(entry);
                if (entryResource != resource) continue;
                storedQuantity = (double)_entryStoredQuantityField.GetValue(entry);
                return true;
            }
            return false;
        }
    }
}
