using LmpClient.Systems.Agency;
using LmpCommon.Message.Data.Agency;
using System;
using System.Reflection;
using System.Security.Cryptography;

namespace LmpClient.Harmony
{
    /// <summary>
    /// [Phase 3 Slice D-2] Shared reflection cache + entry-building helpers
    /// used by the four orbital-logistics Harmony patches
    /// (<see cref="OrbitalLogisticsTransferRequest_DeliverPrefix"/>,
    /// <see cref="OrbitalLogisticsTransferRequest_DeliverPostfix"/>,
    /// <see cref="OrbitalLogisticsTransferRequest_DoFinalLaunchTasksPostfix"/>,
    /// <see cref="OrbitalLogisticsTransferRequest_AbortPostfix"/>).
    ///
    /// <para><b>Why centralised?</b> All four patches need to read the same set
    /// of fields off the dynamically-resolved
    /// <c>KolonyTools.OrbitalLogisticsTransferRequest</c> instance (the type
    /// isn't a compile-time dependency, so the patches receive
    /// <c>object __instance</c> and reflect on it). One cache + one-shot
    /// resolve gate means a single warning on MKS-version-mismatch instead of
    /// four; one entry-builder means uniform behaviour across the postfix
    /// triple.</para>
    ///
    /// <para><b>Brittleness mitigation</b> (pre-spec §6 item 4 + the
    /// per-postfix XML notes): a future MKS rename / signature change is
    /// detected at first resolve and the patches become no-ops for the
    /// session. Single <c>[fix:MKS-R2]</c> warning line so operators can
    /// grep KSP.log alongside the R0 / R1 / R2 namespace. Graceful
    /// degradation matches the Slice B / Slice C
    /// <see cref="ModulePlanetaryLogistics_LevelResourcesPostfix"/> +
    /// <see cref="KolonizationManager_TrackLogEntryPostfix"/> precedent.</para>
    ///
    /// <para><b>TransferGuid derivation.</b> MKS' <c>OrbitalLogisticsTransferRequest</c>
    /// has no stable identifier across scene reloads — <c>ScenarioOrbitalLogistics.OnLoad</c>
    /// constructs fresh instances from the persisted TRANSFER ConfigNode at
    /// each scene boundary. The per-agency wire's partition key
    /// (<see cref="AgencyOrbitalTransferEntry.TransferGuid"/>) needs to be
    /// stable across reloads so the server's
    /// <c>AgencyState.OrbitalTransfers</c> dict upserts idempotently. We
    /// derive a deterministic Guid from the four ConfigNode-persisted
    /// fields that are guaranteed stable across reload:
    /// <c>(OriginVesselId, DestinationVesselId, StartTime, Duration)</c>.
    /// SHA1-hashed into a 16-byte Guid. Collision requires two distinct
    /// transfers between the same Origin/Destination pair with identical
    /// double-precision StartTime AND Duration — structurally impossible
    /// because <c>StartTime = Planetarium.GetUniversalTime()</c> serializes
    /// human-driven launches per-frame.</para>
    ///
    /// <para><b>Threading.</b> All four patches fire on Unity's main thread
    /// (Deliver / DoFinalLaunchTasks / Abort are called from
    /// <c>ScenarioOrbitalLogistics.Update → ProcessTransfers</c> which is a
    /// MonoBehaviour Update). Reflection is single-threaded against the
    /// patch instances. The static fields here are written once at first
    /// resolve and read-only thereafter — no contention.</para>
    /// </summary>
    public static class OrbitalLogisticsReflection
    {
        // Cached reflection handles for the OrbitalLogisticsTransferRequest
        // shape at pinned SHA ed0f6aa6 (verified against
        // F:\tmp\mks-external\MKS\Source\KolonyTools\OrbitalLogistics\OrbitalLogisticsTransferRequest.cs).
        private static Type _transferType;
        private static FieldInfo _statusField;
        private static FieldInfo _statusMessageField;
        private static FieldInfo _startTimeField;
        private static FieldInfo _durationField;
        private static PropertyInfo _originProperty;
        private static PropertyInfo _destinationProperty;
        private static MethodInfo _saveMethod;
        private static bool _resolved;
        private static bool _resolveFailed;

        // Per-site one-shot error log gate. Same intent as Slice B / Slice C
        // (suppress log-flood from a persistent failure mode) but keyed on
        // call-site name so a failure at one site doesn't silence diagnostics
        // from unrelated sites. Two parallel reviewers caught the prior
        // single-bool gate as too wide.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _siteFailureLogged =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        internal static bool TryResolve()
        {
            if (_resolved) return true;
            if (_resolveFailed) return false;

            try
            {
                _transferType = HarmonyLib.AccessTools.TypeByName("KolonyTools.OrbitalLogisticsTransferRequest");
                if (_transferType == null) { _resolveFailed = true; return false; }

                _statusField = _transferType.GetField("Status",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _statusMessageField = _transferType.GetField("StatusMessage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _startTimeField = _transferType.GetField("StartTime",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _durationField = _transferType.GetField("Duration",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _originProperty = _transferType.GetProperty("Origin",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _destinationProperty = _transferType.GetProperty("Destination",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _saveMethod = _transferType.GetMethod("Save",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, types: new[] { typeof(ConfigNode) }, modifiers: null);

                if (_statusField == null || _statusMessageField == null
                    || _startTimeField == null || _durationField == null
                    || _originProperty == null || _destinationProperty == null
                    || _saveMethod == null)
                {
                    // HarmonyPatcher's boot-time check only validates Deliver
                    // / DoFinalLaunchTasks / Abort method handles — if MKS
                    // renames a FIELD or PROPERTY (e.g. Status → State), the
                    // method-resolve check passes but TryResolve silently
                    // fails on first call. Emit a single operator-visible
                    // line naming the missing handles so the symptom isn't
                    // "patches applied but state-machine echoes are silent."
                    var missing = new System.Collections.Generic.List<string>();
                    if (_statusField == null) missing.Add("Status");
                    if (_statusMessageField == null) missing.Add("StatusMessage");
                    if (_startTimeField == null) missing.Add("StartTime");
                    if (_durationField == null) missing.Add("Duration");
                    if (_originProperty == null) missing.Add("Origin");
                    if (_destinationProperty == null) missing.Add("Destination");
                    if (_saveMethod == null) missing.Add("Save");
                    LunaLog.LogWarning(
                        $"[LMP]: [fix:MKS-R2] OrbitalLogisticsTransferRequest reflection-resolve FAILED — " +
                        $"missing handles: {string.Join(", ", missing)}. MKS version mismatch? " +
                        "Per-agency orbital state-machine echoes AND per-frame double-spend prevention BOTH " +
                        "disabled for the session (the patches registered at boot stay no-op).");
                    _resolveFailed = true;
                    return false;
                }

                _resolved = true;
                return true;
            }
            catch (Exception)
            {
                _resolveFailed = true;
                return false;
            }
        }

        /// <summary>
        /// Reads <c>Status</c> off the live transfer instance and returns the
        /// raw <c>int</c> value (the underlying type of MKS'
        /// <c>DeliveryStatus</c> enum). Constants on
        /// <see cref="AgencyOrbitalTransferEntry"/> (<c>StatusLaunched</c>,
        /// <c>StatusReturning</c>, etc.) name the wire values at pinned SHA.
        /// Returns <c>-1</c> on reflection failure — caller treats as "skip
        /// this transition" (no postfix emit).
        /// </summary>
        internal static int ReadStatus(object instance)
        {
            if (instance == null || !TryResolve()) return -1;
            try { return (int)_statusField.GetValue(instance); }
            catch { return -1; }
        }

        /// <summary>
        /// Writes <c>Status</c> on the live transfer instance from an int
        /// value (expected to match the MKS <c>DeliveryStatus</c> enum's
        /// underlying value). The reflection layer converts the int back to
        /// the enum via <see cref="FieldInfo.SetValue"/>'s implicit enum
        /// conversion. Used by the Deliver-prefix on the skip path.
        /// </summary>
        internal static void WriteStatus(object instance, int statusValue)
        {
            if (instance == null || !TryResolve()) return;
            try
            {
                var enumValue = Enum.ToObject(_statusField.FieldType, statusValue);
                _statusField.SetValue(instance, enumValue);
            }
            catch { /* best-effort; logged via caller's failure path */ }
        }

        /// <summary>
        /// Writes <c>StatusMessage</c> (a public string field on MKS' transfer).
        /// </summary>
        internal static void WriteStatusMessage(object instance, string message)
        {
            if (instance == null || !TryResolve()) return;
            try { _statusMessageField.SetValue(instance, message ?? string.Empty); }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Resolves the destination vessel's canonical Guid (<c>vessel.id</c>)
        /// via the transfer's <c>Destination</c> property — which internally
        /// runs both the persistentId match AND the Guid match per MKS source
        /// <c>OrbitalLogisticsTransferRequest.cs:127-145</c>, then falls back
        /// to <c>FindVesselByOrbLogModuleId</c>. Returns <see cref="Guid.Empty"/>
        /// when the underlying Vessel reference is null (vessel destroyed /
        /// transient post-unload).
        ///
        /// <para><b>persistentId vs Guid asymmetry (load-bearing).</b> MKS'
        /// <c>Destination</c> setter (line 149) writes
        /// <c>value.persistentId.ToString()</c> into the <c>_destinationId</c>
        /// backing field — NOT the canonical Guid. The property GETTER
        /// (lines 127-145) compensates by trying BOTH
        /// <c>persistentId.ToString() == _destinationId</c> AND
        /// <c>v.id.ToString() == _destinationId</c>, so a `Destination`
        /// access returns a real <see cref="Vessel"/> regardless of which
        /// form was written. We then read <c>vessel.id</c> — the canonical
        /// Guid LMP uses for <c>VesselStoreSystem</c> keying and for the
        /// Stage 5.16b <c>OwningAgencyId</c> stamp. If a future MKS refactor
        /// stops persisting <c>_destinationId</c> as <c>persistentId.ToString()</c>
        /// (e.g., moves to a Guid-typed field), the property getter still
        /// works on the alternate match path — but the brittleness surface
        /// shifts. Next-author updating MKS source pin: cross-check both
        /// the setter at line 149 AND the getter at line 127-145.</para>
        /// </summary>
        internal static Guid GetDestinationVesselId(object instance)
        {
            return GetVesselIdFromProperty(instance, _destinationProperty);
        }

        /// <summary>
        /// Mirror of <see cref="GetDestinationVesselId"/> for the Origin side.
        /// </summary>
        internal static Guid GetOriginVesselId(object instance)
        {
            return GetVesselIdFromProperty(instance, _originProperty);
        }

        private static Guid GetVesselIdFromProperty(object instance, PropertyInfo prop)
        {
            if (instance == null || prop == null || !TryResolve()) return Guid.Empty;
            try
            {
                var vessel = prop.GetValue(instance, null) as Vessel;
                return vessel?.id ?? Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        internal static double ReadStartTime(object instance)
        {
            if (instance == null || !TryResolve()) return 0d;
            try { return (double)_startTimeField.GetValue(instance); }
            catch { return 0d; }
        }

        internal static double ReadDuration(object instance)
        {
            if (instance == null || !TryResolve()) return 0d;
            try { return (double)_durationField.GetValue(instance); }
            catch { return 0d; }
        }

        /// <summary>
        /// Serializes the transfer via MKS' own <c>Save(ConfigNode)</c> method
        /// at <c>OrbitalLogisticsTransferRequest.cs:658-670</c>, returning the
        /// resulting TRANSFER ConfigNode as UTF-8 bytes. The server-side
        /// projector splice (<c>AgencyScenarioProjector.SpliceAgencyOrbitalTransfers</c>,
        /// Slice D-1) deserializes back via the same ConfigNode round-trip.
        /// </summary>
        internal static byte[] SerializeTransferToBytes(object instance)
        {
            if (instance == null || !TryResolve()) return Array.Empty<byte>();
            try
            {
                var node = new ConfigNode("TRANSFER");
                _saveMethod.Invoke(instance, new object[] { node });
                var text = node.ToString();
                return System.Text.Encoding.UTF8.GetBytes(text);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Derives a stable <see cref="Guid"/> from the four ConfigNode-
        /// persisted fields that survive scene reload. See class XML
        /// "TransferGuid derivation" for the collision argument.
        ///
        /// <para><b>Endian-safety contract.</b> <see cref="Guid.ToByteArray"/>
        /// is documented to return the Microsoft little-endian layout for
        /// the first three fields regardless of host endianness, so the
        /// Guid bytes are stable across any host. We pair that with
        /// <see cref="WriteDoubleLittleEndian"/> for <paramref name="startTime"/>
        /// and <paramref name="duration"/> to make the double bytes
        /// equally host-endianness-independent. The whole SHA1 input
        /// buffer is therefore byte-identical across hosts for the same
        /// logical inputs — derived <see cref="Guid"/> stays stable
        /// across scene reloads and across heterogeneous-arch cohorts.</para>
        /// </summary>
        internal static Guid DeriveTransferGuid(Guid originVesselId, Guid destinationVesselId, double startTime, double duration)
        {
            // Empty origin/destination yields zero hash inputs → stable Empty
            // result. The router rejects Empty TransferGuid (Slice D-1
            // AgencyOrbitalRouter.cs:198), so a pathological empty-id input
            // produces a no-op router rejection instead of a wire emit with a
            // junk guid.
            if (originVesselId == Guid.Empty && destinationVesselId == Guid.Empty
                && startTime == 0d && duration == 0d)
                return Guid.Empty;

            var buf = new byte[16 + 16 + 8 + 8];
            Buffer.BlockCopy(originVesselId.ToByteArray(), 0, buf, 0, 16);
            Buffer.BlockCopy(destinationVesselId.ToByteArray(), 0, buf, 16, 16);
            // Endian-safe: convert double → int64 bit-pattern → little-endian
            // bytes explicitly so the derived Guid stays stable if a future
            // cohort member runs on big-endian hardware (defensive; today's
            // x64 desktop targets are all little-endian, but the wire +
            // partition-key stability claim shouldn't quietly depend on
            // host endianness).
            WriteDoubleLittleEndian(buf, 32, startTime);
            WriteDoubleLittleEndian(buf, 40, duration);

            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(buf);
                var guidBytes = new byte[16];
                Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);
                return new Guid(guidBytes);
            }
        }

        private static void WriteDoubleLittleEndian(byte[] dest, int offset, double value)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            dest[offset + 0] = (byte)(bits);
            dest[offset + 1] = (byte)(bits >> 8);
            dest[offset + 2] = (byte)(bits >> 16);
            dest[offset + 3] = (byte)(bits >> 24);
            dest[offset + 4] = (byte)(bits >> 32);
            dest[offset + 5] = (byte)(bits >> 40);
            dest[offset + 6] = (byte)(bits >> 48);
            dest[offset + 7] = (byte)(bits >> 56);
        }

        /// <summary>
        /// Builds an <see cref="AgencyOrbitalTransferEntry"/> from the live
        /// transfer instance. Used by the three state-machine postfixes.
        /// Returns false when reflection fails or the destination vessel
        /// can't be resolved (the router rejects empty DestinationVesselId
        /// anyway — see <c>AgencyOrbitalRouter.cs:211</c> — and emitting a
        /// known-reject entry just wastes a wire round-trip).
        /// </summary>
        /// <param name="instance">The MKS
        ///   <c>OrbitalLogisticsTransferRequest</c> as <see cref="object"/>
        ///   (Harmony patches receive it un-typed; reflection reads the
        ///   fields).</param>
        /// <param name="entry">The constructed entry on success; null on
        ///   failure.</param>
        internal static bool TryBuildEntry(object instance, out AgencyOrbitalTransferEntry entry)
        {
            entry = null;
            if (instance == null || !TryResolve()) return false;
            try
            {
                var originId = GetOriginVesselId(instance);
                var destId = GetDestinationVesselId(instance);
                if (destId == Guid.Empty) return false;

                var status = ReadStatus(instance);
                if (status < 0) return false;

                var startTime = ReadStartTime(instance);
                var duration = ReadDuration(instance);
                var payload = SerializeTransferToBytes(instance);
                var transferGuid = DeriveTransferGuid(originId, destId, startTime, duration);

                entry = new AgencyOrbitalTransferEntry
                {
                    TransferGuid = transferGuid,
                    OriginVesselId = originId,
                    DestinationVesselId = destId,
                    Status = status,
                    StartTime = startTime,
                    Duration = duration,
                    PayloadBytes = payload,
                    NumBytes = payload?.Length ?? 0,
                };
                return true;
            }
            catch (Exception ex)
            {
                LogRuntimeFailureOnce("TryBuildEntry", ex);
                return false;
            }
        }

        /// <summary>
        /// One-shot error log for runtime failure during postfix / prefix
        /// reflection. Matches Slice B / Slice C
        /// <c>_postfixRuntimeFailureLogged</c> gate — a persistent failure
        /// mode mustn't flood KSP.log every FixedUpdate.
        /// </summary>
        internal static void LogRuntimeFailureOnce(string site, Exception ex)
        {
            if (string.IsNullOrEmpty(site)) site = "unknown";
            // TryAdd returns false if the key already exists — i.e. this site
            // has logged at least once this session. Subsequent failures at
            // the same site silence; first failure at a DIFFERENT site still
            // logs (the prior single-bool gate would have suppressed it).
            if (!_siteFailureLogged.TryAdd(site, true)) return;
            LunaLog.LogError(
                $"[LMP]: [fix:MKS-R2] Orbital-logistics Harmony runtime failure at {site} (once-per-site log): {ex.GetType().Name}: {ex.Message}. " +
                "Per-agency orbital state-machine echoes at this site DROP silently until KSP is restarted; investigate MKS / LMP-fork version mismatch.");
        }
    }
}
