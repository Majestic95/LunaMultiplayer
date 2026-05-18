using System;
using System.Globalization;
using System.Reflection;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Data.Agency;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 3 Slice B] MKS-R2 — Postfix on
    /// <c>KolonyTools.KolonizationManager.TrackLogEntry(KolonizationEntry)</c>.
    /// Mirrors every per-vessel kolony research mutation into the per-agency
    /// wire so the server-side <c>AgencyKolonyRouter</c> can route + persist +
    /// echo + project the mutation out only to the owning agency.
    ///
    /// <para><b>Hook anchor + Q1 resolution.</b> The postfix sits on
    /// <c>KolonizationManager.TrackLogEntry</c> — the manager-singleton mutator.
    /// All entry sources (MKS' <c>MKSModule.UpdateGoals</c> at FixedUpdate
    /// cadence, <c>FetchLogEntry</c> at lazy-create time, AND
    /// <c>ModuleColonyRewards.CheckRewards</c> at line 33) call this method
    /// directly on the singleton, so a single postfix catches every entry
    /// source uniformly. Verified at pre-spec §11 Q1 + this slice's source-
    /// level read of <c>F:\tmp\mks-external\MKS\Source\KolonyTools\ModuleColonyRewards.cs:33</c>.</para>
    ///
    /// <para><b>Brittleness mitigation (pre-spec §6).</b> Patch is registered
    /// imperatively via <see cref="LmpClient.Base.HarmonyPatcher.PatchKolonizationManager"/>
    /// because <c>KolonyTools</c> is not a compile-time dep. <see cref="object"/>-
    /// typed parameter on the postfix lets Harmony bind to a private/unknown-
    /// at-compile-time type. The patch is a no-op if MKS isn't installed or the
    /// type/method was renamed — graceful degradation matches the MKS-R0 +
    /// MKS-R1 self-disable pattern, single source of truth in the
    /// <c>[fix:MKS-R[012]]</c> grep namespace.</para>
    ///
    /// <para><b>Gate.</b> Under <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = false</c>
    /// the postfix is a no-op (no wire emit, no state mutation suppression).
    /// The legacy 30s SHA pass on <c>KolonizationScenario</c> covers shared-mode
    /// propagation unchanged — strict dual-mode silence. The
    /// <c>IgnoredScenarios</c> Option B filter (Slice B item 10) provides the
    /// counterpart broadcast suppression under gate=on so the postfix + the
    /// projector are the SOLE kolony-data path under per-agency mode.</para>
    ///
    /// <para><b>Reflection cost.</b> The 13 entry-field reads run per postfix
    /// invocation. Cached <see cref="FieldInfo"/> references avoid the
    /// repeated <c>Type.GetField</c> lookup. Under heavy MKS load (50+
    /// converter parts on a megabase) per pre-spec §11 Q6, the postfix
    /// fires per <c>FixedUpdate</c> per converter — the cache caps the
    /// reflective overhead at one virtual-call + one field-read-by-pointer
    /// per field per call. If telemetry flags this as a hotspot, the
    /// follow-up is a per-batch coalescing layer at
    /// <see cref="AgencyKolonySender"/> (collect entries on one tick,
    /// batch-send next tick) — deferred until measured.</para>
    /// </summary>
    public static class KolonizationManager_TrackLogEntryPostfix
    {
        // Cached FieldInfo handles. Populated lazily on first invocation when
        // the entry's runtime type is observed; subsequent calls reuse. Note:
        // MKS field names are pinned at SHA ed0f6aa6
        // (F:\tmp\mks-external\MKS\Source\KolonyTools\Kolonization\KolonizationEntry.cs).
        private static FieldInfo _vesselIdField;
        private static FieldInfo _bodyIndexField;
        private static FieldInfo _lastUpdateField;
        private static FieldInfo _kolonyDateField;
        private static FieldInfo _geologyResearchField;
        private static FieldInfo _botanyResearchField;
        private static FieldInfo _kolonizationResearchField;
        private static FieldInfo _scienceField;
        private static FieldInfo _repField;
        private static FieldInfo _fundsField;
        private static FieldInfo _repBoostersField;
        private static FieldInfo _fundsBoostersField;
        private static FieldInfo _scienceBoostersField;
        private static bool _fieldsResolved;
        private static bool _fieldsResolveFailed;

        // One-shot error log gate (round-1 general-lens CONSIDER C1). Without
        // this, a runtime exception inside the postfix's field-extraction loop
        // would silently drop entries forever — the operator's only signal is
        // "my per-agency kolony state isn't syncing." A single LunaLog.Error on
        // first failure surfaces the symptom; the silent-drop posture after
        // that keeps the hot-path postfix from log-spamming on a persistent
        // failure mode.
        private static bool _postfixRuntimeFailureLogged;

        /// <summary>
        /// Postfix entry point. Harmony binds the <c>logEntry</c> parameter
        /// positionally to MKS' <c>KolonizationEntry</c> — declared as
        /// <see cref="object"/> here so this file compiles without a
        /// KolonyTools reference.
        /// </summary>
        internal static void Postfix(object logEntry)
        {
            // Gate. Cheap early-return under shared mode + Sandbox / Science.
            if (!SettingsSystem.ServerSettings.PerAgencyCareerEnabled) return;
            if (logEntry == null) return;

            try
            {
                if (!TryResolveFields(logEntry.GetType())) return;

                var entry = new AgencyKolonyEntry
                {
                    VesselId = NormalizeVesselId((string)_vesselIdField.GetValue(logEntry)),
                    BodyIndex = (int)_bodyIndexField.GetValue(logEntry),
                    LastUpdate = (double)_lastUpdateField.GetValue(logEntry),
                    KolonyDate = (double)_kolonyDateField.GetValue(logEntry),
                    GeologyResearch = (double)_geologyResearchField.GetValue(logEntry),
                    BotanyResearch = (double)_botanyResearchField.GetValue(logEntry),
                    KolonizationResearch = (double)_kolonizationResearchField.GetValue(logEntry),
                    Science = (double)_scienceField.GetValue(logEntry),
                    Reputation = (double)_repField.GetValue(logEntry),  // MKS field is "Rep"; LMP field is "Reputation"
                    Funds = (double)_fundsField.GetValue(logEntry),
                    RepBoosters = (int)_repBoostersField.GetValue(logEntry),
                    FundsBoosters = (int)_fundsBoostersField.GetValue(logEntry),
                    ScienceBoosters = (int)_scienceBoostersField.GetValue(logEntry),
                };

                AgencyKolonySender.SendMutation(entry);
            }
            catch (Exception ex)
            {
                // Per-postfix exception isolation. A single failed extraction
                // must not crash MKS' own TrackLogEntry path (we're a postfix —
                // the original already ran successfully). [Round-1 general-lens
                // CONSIDER C1]: log ONCE at LunaLog.Log level so operators have
                // a grep target if per-agency kolony sync stops working; silent
                // after the first occurrence so the hot-path postfix doesn't
                // flood KSP.log on a persistent failure.
                if (!_postfixRuntimeFailureLogged)
                {
                    _postfixRuntimeFailureLogged = true;
                    LunaLog.LogError($"[LMP]: [fix:MKS-R2] KolonizationManager.TrackLogEntry postfix runtime failure (once-only log): {ex.GetType().Name}: {ex.Message}. Per-agency kolony sync is now DROPPING entries silently until KSP is restarted; investigate MKS / LMP-fork version mismatch.");
                }
            }
        }

        /// <summary>
        /// MKS' <c>KolonizationEntry.VesselId</c> is a string. Most callers
        /// already pass <c>vessel.id.ToString()</c> (which is "D" form with
        /// hyphens — a Guid). The server-side router normalizes to "N" form
        /// (no hyphens) so dict keys converge. We do the same normalisation
        /// client-side so the wire payload is already canonical — minor
        /// redundancy with the router's normalisation, but means the wire
        /// + the persisted disk file agree even if a future router
        /// regression drops the normalisation step.
        /// </summary>
        private static string NormalizeVesselId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            return Guid.TryParse(raw, out var g)
                ? g.ToString("N", CultureInfo.InvariantCulture)
                : raw;
        }

        /// <summary>
        /// First-call field-info resolution. Caches all 13 <see cref="FieldInfo"/>
        /// references per type. Returns false if MKS' <c>KolonizationEntry</c>
        /// shape doesn't match the pinned SHA — postfix becomes a no-op for the
        /// rest of the session (operator must restart KSP after updating MKS
        /// to a matching version, or the patch will need a slice-B-style
        /// follow-up).
        /// </summary>
        private static bool TryResolveFields(Type entryType)
        {
            if (_fieldsResolved) return true;
            if (_fieldsResolveFailed) return false;
            if (entryType == null) { _fieldsResolveFailed = true; return false; }

            // BindingFlags: MKS uses public auto-properties, which generate
            // private backing fields with mangled names. Reflect on the
            // properties' setter-target or use the public Property reads via
            // PropertyInfo. Simpler: read via PropertyInfo since MKS exposes
            // all 13 fields as public { get; set; } properties.
            try
            {
                _vesselIdField = ResolveField(entryType, "VesselId", typeof(string));
                _bodyIndexField = ResolveField(entryType, "BodyIndex", typeof(int));
                _lastUpdateField = ResolveField(entryType, "LastUpdate", typeof(double));
                _kolonyDateField = ResolveField(entryType, "KolonyDate", typeof(double));
                _geologyResearchField = ResolveField(entryType, "GeologyResearch", typeof(double));
                _botanyResearchField = ResolveField(entryType, "BotanyResearch", typeof(double));
                _kolonizationResearchField = ResolveField(entryType, "KolonizationResearch", typeof(double));
                _scienceField = ResolveField(entryType, "Science", typeof(double));
                _repField = ResolveField(entryType, "Rep", typeof(double));  // MKS uses "Rep" (not Reputation)
                _fundsField = ResolveField(entryType, "Funds", typeof(double));
                _repBoostersField = ResolveField(entryType, "RepBoosters", typeof(int));
                _fundsBoostersField = ResolveField(entryType, "FundsBoosters", typeof(int));
                _scienceBoostersField = ResolveField(entryType, "ScienceBoosters", typeof(int));

                _fieldsResolved = true;
                return true;
            }
            catch (Exception)
            {
                _fieldsResolveFailed = true;
                return false;
            }
        }

        /// <summary>
        /// MKS' <c>KolonizationEntry</c> declares all 13 members as public
        /// auto-properties (<c>{ get; set; }</c>). Auto-properties generate a
        /// compiler-mangled backing field like
        /// <c>&lt;BodyIndex&gt;k__BackingField</c>. We try the backing field
        /// first (lower overhead on GetValue) and fall back to the property's
        /// MethodInfo.Invoke if the type was hand-coded with explicit fields.
        /// Both paths return a <see cref="FieldInfo"/>-like accessor —
        /// implemented here as a uniform <see cref="FieldInfo"/> reflective
        /// path for simplicity (auto-property backing fields ARE
        /// <see cref="FieldInfo"/>).
        /// </summary>
        private static FieldInfo ResolveField(Type entryType, string memberName, Type expectedType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // Try the auto-property backing field first.
            var backing = entryType.GetField($"<{memberName}>k__BackingField", flags);
            if (backing != null && backing.FieldType == expectedType) return backing;

            // Fall back to a direct public field of the same name (in case a
            // future MKS rewrite removes the auto-property in favour of an
            // explicit field).
            var direct = entryType.GetField(memberName, flags);
            if (direct != null && direct.FieldType == expectedType) return direct;

            // No match — let the caller treat this as a resolve failure.
            throw new InvalidOperationException(
                $"[fix:MKS-R2] KolonizationEntry member '{memberName}' (type {expectedType.Name}) not found on {entryType.FullName}");
        }
    }
}
