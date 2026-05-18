using Server.Command.Command.Base;
using Server.Log;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Stage 5.18d slice (d). Read-only operator surface: enumerate the per-agency career
    /// registry. Every line is tagged <c>[fix:per-agency-career]</c> so the Stage 5.18+
    /// GUI launcher (which wraps server stdout) can correlate emissions across the
    /// LunaLog timestamp prefix + interleaved background log noise. See
    /// <see cref="ListAgenciesFormatter"/>'s class XML for the full output framing and
    /// the parsing contract.
    ///
    /// <para><b>Three operational states</b> the formatter distinguishes:</para>
    /// <list type="number">
    ///   <item><description><c>PerAgencyCareer=false</c> → disabled status line + (when N&gt;0)
    ///   a stranded-stamps line + recovery hint.</description></item>
    ///   <item><description><c>PerAgencyCareer=true</c> AND <c>GameMode≠Career</c> →
    ///   inactive warning line + full enumeration block with <c>state=inactive</c> on the
    ///   start line.</description></item>
    ///   <item><description>Gate active (configured AND Career) → enumeration block with
    ///   <c>state=live</c>.</description></item>
    /// </list>
    ///
    /// <para>Mirrors the boot-time helpers <c>WarnAboutStrandedAgencyStampsIfGateOff</c>,
    /// <c>WarnAboutOrphanedVessels</c>, and the inactive-mode warning in
    /// <see cref="AgencySystem.LoadExistingAgencies"/> — same problem statements, same
    /// recovery paths, so an operator reading <c>/listagencies</c> output mid-session
    /// gets information consistent with what they saw at boot.</para>
    /// </summary>
    public class ListAgenciesCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var perAgencyConfigured = GameplaySettings.SettingsStore.PerAgencyCareer;
            var gateActive = AgencySystem.PerAgencyEnabled;
            var acceptedLossOverride = GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse;

            // Snapshot Agencies.Values into a list before either tally pass. ConcurrentDictionary
            // .Values is moment-in-time-safe, but a stable snapshot guarantees the orphan-vs-known
            // partition below sees the SAME registry membership as the per-agency snapshot loop
            // further down. Without the snapshot, a RegisterAgency landing mid-method could
            // leave a vessel counted as "orphan" in one pass and "owned by agency X" in the
            // other — confusingly self-inconsistent in one output block.
            //
            // Acknowledged transient: a brand-new agency that registers AFTER the snapshot
            // but BEFORE the partition loop will see its existing vessels classified as
            // orphans for this invocation only. Self-corrects on the next /listagencies call.
            // Acceptable for a diagnostic command (no operator action would be taken based
            // on a single transient output).
            var agencySnapshot = AgencySystem.Agencies.Values.ToList();
            var knownAgencyIds = new HashSet<Guid>();
            foreach (var a in agencySnapshot)
                knownAgencyIds.Add(a.AgencyId);

            // Three-way vessel-tally pass:
            //   (a) OwningAgencyId == Guid.Empty → unassigned-sentinel (spec §10 Q3)
            //   (b) OwningAgencyId in knownAgencyIds → counted into vesselCounts (per-agency)
            //   (c) OwningAgencyId non-Empty + not in registry → orphan (boot warning surface)
            //
            // The vessel store is orthogonal to AgencyState — vessel.OwningAgencyId lives on
            // the Vessel object; a concurrent SaveAgency on agency X can't change any vessel's
            // OwningAgencyId. A vessel proto landing mid-pass might shift a count by 1, which
            // is acceptable display jitter and self-corrects on the next /listagencies.
            var vesselCounts = new Dictionary<Guid, int>();
            var orphanCounts = new Dictionary<Guid, int>();
            var unassignedVessels = 0;
            foreach (var vessel in VesselStoreSystem.CurrentVessels.Values)
            {
                var owner = vessel.OwningAgencyId;
                if (owner == Guid.Empty)
                {
                    unassignedVessels++;
                    continue;
                }
                if (knownAgencyIds.Contains(owner))
                {
                    vesselCounts.TryGetValue(owner, out var c);
                    vesselCounts[owner] = c + 1;
                }
                else
                {
                    orphanCounts.TryGetValue(owner, out var c);
                    orphanCounts[owner] = c + 1;
                }
            }

            // Per-agency snapshot: take the agency-specific lock around field reads so a
            // concurrent SaveAgency doesn't serialise a torn intermediate snapshot of the
            // scalars while we copy them. Same caller-cooperation contract as
            // ScenarioDataUpdater.GetSemaphore.
            var rows = new List<ListAgenciesFormatter.AgencyRow>(agencySnapshot.Count);
            foreach (var agency in agencySnapshot)
            {
                lock (AgencySystem.GetAgencyLock(agency.AgencyId))
                {
                    rows.Add(new ListAgenciesFormatter.AgencyRow
                    {
                        AgencyId = agency.AgencyId,
                        OwningPlayerName = agency.OwningPlayerName ?? string.Empty,
                        DisplayName = agency.DisplayName ?? string.Empty,
                        Funds = agency.Funds,
                        Science = agency.Science,
                        Reputation = agency.Reputation,
                        VesselCount = vesselCounts.TryGetValue(agency.AgencyId, out var n) ? n : 0,
                    });
                }
            }

            var orphans = orphanCounts
                .Select(kvp => new ListAgenciesFormatter.OrphanRow { OrphanAgencyId = kvp.Key, VesselCount = kvp.Value })
                .ToList();

            foreach (var line in ListAgenciesFormatter.Format(
                rows, orphans, unassignedVessels,
                perAgencyConfigured, gateActive, acceptedLossOverride))
            {
                LunaLog.Normal(line);
            }

            return true;
        }
    }
}
