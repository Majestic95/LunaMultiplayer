using LmpCommon.Time;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Server.System
{
    public static class WarpSystem
    {
        private static string SubspaceFile { get; } = Path.Combine(ServerContext.UniverseDirectory, "Subspace.txt");

        public static void Reset()
        {
            WarpContext.Subspaces.Clear();
            LoadSavedSubspace();
        }

        public static void BackupSubspaces()
        {
            var content = $"#Incorrectly editing this file will cause weirdness. If there is any errors, the universe time will be reset.{Environment.NewLine}";
            content += $"#This file can only be edited if the server is stopped.{Environment.NewLine}";
            content += $"#It must always contain ONLY 1 subspace which will be the most advanced in the future{Environment.NewLine}";
            content += $"#The value is defined as: subspaceId:server_time_difference_in_seconds.{Environment.NewLine}";

            content += $"{WarpContext.LatestSubspace}";

            FileHandler.WriteToFile(SubspaceFile, content);
        }

        /// <summary>
        /// Long-running task that periodically refreshes each subspace's Solo flag and broadcasts the
        /// transition to all clients. See BUG-001 (docs/research/02-analysis/bug-001-solo-subspace-catchup.md).
        /// Cadence is controlled by IntervalSettings.SoloSubspaceCheckMs; 0 disables the loop entirely
        /// (catch-up snap stays active for everyone).
        /// </summary>
        public static async Task PerformSoloSubspaceChecksAsync(CancellationToken token)
        {
            while (ServerContext.ServerRunning)
            {
                var intervalMs = IntervalSettings.SettingsStore.SoloSubspaceCheckMs;
                if (intervalMs <= 0)
                {
                    try { await Task.Delay(TimeSpan.FromMinutes(1), token); }
                    catch (TaskCanceledException) { break; }
                    continue;
                }

                try { await Task.Delay(intervalMs, token); }
                catch (TaskCanceledException) { break; }

                try
                {
                    RefreshSoloStatuses();
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[fix:BUG-001] RefreshSoloStatuses failed: {e}");
                }
            }
        }

        /// <summary>
        /// For each known subspace, compute whether exactly one client occupies it; if the flag changed
        /// since the last check, broadcast a WarpSubspaceSoloStatusMsgData to all clients. The check
        /// itself is read-only — clients see the transition via the broadcast. Pure for testability:
        /// the per-subspace count + transition-detect logic is exposed via DetectSoloTransitions so the
        /// test suite can exercise it without a running server.
        /// </summary>
        public static void RefreshSoloStatuses()
        {
            var subspaces = WarpContext.Subspaces.Values.ToArray();
            var clientSubspaceIds = ServerContext.Clients.Values.Select(c => c.Subspace).ToArray();

            foreach (var transition in DetectSoloTransitions(subspaces, clientSubspaceIds))
            {
                transition.Subspace.Solo = transition.NewSolo;
                LunaLog.Debug($"[fix:BUG-001] subspace {transition.Subspace.Id} solo flag -> {transition.NewSolo}");
                WarpSystemSender.SendSubspaceSoloStatus(transition.Subspace.Id, transition.NewSolo);
            }
        }

        /// <summary>
        /// Pure helper: for each input subspace, return the new solo state if it differs from the
        /// subspace's current Solo flag. Exposed for testability; callers in production go through
        /// RefreshSoloStatuses which also commits and broadcasts.
        /// </summary>
        public static IEnumerable<SoloTransition> DetectSoloTransitions(IReadOnlyCollection<Subspace> subspaces, IReadOnlyCollection<int> clientSubspaceIds)
        {
            foreach (var subspace in subspaces)
            {
                if (subspace == null) continue;

                var occupants = 0;
                foreach (var s in clientSubspaceIds)
                {
                    if (s == subspace.Id) occupants++;
                }

                var newSolo = occupants == 1;
                if (newSolo != subspace.Solo)
                {
                    yield return new SoloTransition(subspace, newSolo);
                }
            }
        }

        public readonly struct SoloTransition
        {
            public readonly Subspace Subspace;
            public readonly bool NewSolo;

            public SoloTransition(Subspace subspace, bool newSolo)
            {
                Subspace = subspace;
                NewSolo = newSolo;
            }
        }

        /// <summary>
        /// Returns true when <paramref name="candidateSubspace"/> runs strictly earlier in time
        /// than <paramref name="referenceSubspace"/> per the server's recorded
        /// <see cref="Subspace.Time"/> deltas. Sentinels (-1 warping, 0 no-auth, unknown subspace ids)
        /// are NOT considered "past" — they're treated as inert. Used by BUG-005/006 vessel-authority
        /// and lock-acquire checks to reject cross-subspace operations from a past timeline.
        /// </summary>
        public static bool IsStrictlyPast(int candidateSubspace, int referenceSubspace)
        {
            if (candidateSubspace == referenceSubspace) return false;
            if (candidateSubspace <= 0 || referenceSubspace <= 0) return false;

            if (!WarpContext.Subspaces.TryGetValue(candidateSubspace, out var cand)) return false;
            if (!WarpContext.Subspaces.TryGetValue(referenceSubspace, out var refS)) return false;

            return cand.Time < refS.Time;
        }

        public static bool RemoveSubspace(int subspaceToRemove)
        {
            //Do not remove the subspace if there are clients there
            if (ServerContext.Clients.Any(c => c.Value.Subspace == subspaceToRemove))
                return false;

            //If there's only 1 subspace do not remove it!
            if (WarpContext.Subspaces.Count == 1)
                return false;

            //We are in the latest subspace and we NEVER remove it!
            if (subspaceToRemove == WarpContext.LatestSubspace.Id)
                return false;

            //BUG-005/006: do not remove a subspace that is still the AuthoritativeSubspaceId for at
            //least one vessel — pruning would orphan that vessel's lock semantics. The check is
            //O(n_vessels) per disconnect; acceptable at typical server scales (<<10k vessels).
            //See docs/research/02-analysis/bug-005-006-cross-subspace-lock.md.
            if (VesselStoreSystem.CurrentVessels.Values.Any(v => v.AuthoritativeSubspaceId == subspaceToRemove))
            {
                LunaLog.Debug($"[fix:BUG-005/006] refusing to remove subspace '{subspaceToRemove}' — at least one vessel still authoritative there");
                return false;
            }

            LunaLog.Debug($"Removing abandoned subspace '{subspaceToRemove}'");
            WarpContext.Subspaces.TryRemove(subspaceToRemove, out _);
            return true;
        }

        #region Private methods

        private static void LoadSavedSubspace()
        {
            if (FileHandler.FileExists(SubspaceFile))
            {
                var latestStoredSubspace = GetLatestSubspaceLineFromFile();
                WarpContext.Subspaces.TryAdd(latestStoredSubspace.Key, new Subspace(latestStoredSubspace.Key, latestStoredSubspace.Value));
                WarpContext.NextSubspaceId = WarpContext.Subspaces.Max(s => s.Key) + 1;
            }
            else
            {
                LunaLog.Debug("Creating new subspace file");
                WarpContext.Subspaces.TryAdd(0, new Subspace(0));
                WarpContext.NextSubspaceId = 1;
            }
        }

        private static KeyValuePair<int, double> GetLatestSubspaceLineFromFile()
        {
            var subspaceLines = FileHandler.ReadFileLines(SubspaceFile)
                .Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                .Select(s => new KeyValuePair<int, double>(int.Parse(s.Split(':')[0]), double.Parse(s.Split(':')[1], CultureInfo.InvariantCulture)))
                .ToArray();

            if (subspaceLines.Length == 0)
            {
                LunaLog.Error("Incorrect Subspace.txt file!");
                return new KeyValuePair<int, double>(0, 0);
            }

            if (subspaceLines.Length > 1)
            {
                LunaLog.Error("Subspace.txt should not contain more than 1 subspace line!");
                return subspaceLines.OrderByDescending(s => s.Value).First();
            }

            return subspaceLines.First();
        }

        /// <summary>
        /// Returns the time difference of the given subspace against the server time in ticks
        /// </summary>
        public static long GetSubspaceTimeDifference(int subspace)
        {
            return WarpContext.Subspaces.ContainsKey(subspace) ? TimeUtil.SecondsToTicks(WarpContext.Subspaces[subspace].Time) : 0;
        }

        /// <summary>
        /// Returns the time in ticks at the given subspace
        /// </summary>
        public static long GetSubspaceTime(int subspace)
        {
            return LunaNetworkTime.UtcNow.Ticks + GetSubspaceTimeDifference(subspace);
        }

        /// <summary>
        /// Returns the subspaces that runs in an earlier time (this means the subspaces that have a LOWER time difference)
        /// </summary>
        public static int[] GetPastSubspaces(int subspace)
        {
            if (!WarpContext.Subspaces.ContainsKey(subspace))
                return new int[0];

            return WarpContext.Subspaces.Values.Where(s => s.Id != subspace && WarpContext.Subspaces.TryGetValue(subspace, out var anotherSubspace) && s.Time < anotherSubspace.Time)
                .Select(s => s.Id).ToArray();
        }

        /// <summary>
        /// Returns the subspaces that runs in an future time (this means the subspaces that have a HIGHER time difference)
        /// </summary>
        public static int[] GetFutureSubspaces(int subspace)
        {
            return WarpContext.Subspaces.Values.Where(s => s.Id != subspace && WarpContext.Subspaces.TryGetValue(subspace, out var anotherSubspace) && s.Time > anotherSubspace.Time)
                .Select(s => s.Id).ToArray();
        }

        /// <summary>
        /// Returns the empty subspaces. Caution as here the latest subspace can be included!
        /// </summary>
        public static int[] GetEmptySubspaces()
        {
            return WarpContext.Subspaces.ToArray().Where(s => !ServerContext.Clients.Any(c => c.Value.Subspace == s.Key)).Select(s => s.Key).ToArray();
        }

        #endregion

    }
}
