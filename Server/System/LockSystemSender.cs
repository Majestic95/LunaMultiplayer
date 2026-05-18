using LmpCommon.Locks;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Server.System
{
    public class LockSystemSender
    {
        /// <summary>
        /// Stage 5.18d slice (c) consumer-lens v1 rate-limit. Per-(player,
        /// vessel, lockType) timestamp of the last cross-agency
        /// <see cref="LockRejectMsgData"/> emission. KSP's stock client-side
        /// auto-acquire on physics-range entry + Tracking Station / Map
        /// view-hover can fire many lock-acquires per second; without the
        /// debounce, every reject would stack a 5s
        /// <c>LunaScreenMsg.UPPER_CENTER</c> toast on the player's
        /// screen, obscuring the playfield. The Reject is still SUPPRESSED
        /// on the wire (Lidgren cost saved); the underlying AcquireLock
        /// rejection itself is unaffected.
        ///
        /// <para><b>Memory bound.</b> One entry per distinct (player,
        /// vessel, lockType) combination ever cross-agency-rejected. For
        /// a 10-player × 1000-vessel × 3-lock-type universe, max ~30k
        /// entries × ~50 bytes = ~1.5MB. Bounded by realistic load. No
        /// GC pass; future polish could add a periodic prune for stale
        /// entries older than the debounce window.</para>
        /// </summary>
        internal static readonly ConcurrentDictionary<(string PlayerName, Guid VesselId, LmpCommon.Locks.LockType Type), long> CrossAgencyRejectLastEmitMs =
            new ConcurrentDictionary<(string, Guid, LmpCommon.Locks.LockType), long>();

        /// <summary>
        /// Minimum interval between cross-agency Reject emissions for the
        /// same (player, vessel, lockType) key. 5s matches the toast's own
        /// <see cref="LunaScreenMsg"/> display duration — at most one toast
        /// per same-key reject cluster.
        /// </summary>
        internal const int CrossAgencyRejectMinIntervalMs = 5000;

        /// <summary>
        /// Pure rate-limit decision helper. ServerTest pins each branch
        /// without spinning up the full sender. Returns true when the
        /// caller should emit the Reject (and records the timestamp);
        /// false when the (player, vessel, type) key is still inside the
        /// debounce window (caller skips the emission).
        /// </summary>
        internal static bool ShouldEmitCrossAgencyReject(
            ConcurrentDictionary<(string PlayerName, Guid VesselId, LmpCommon.Locks.LockType Type), long> lastEmitMs,
            string playerName,
            Guid vesselId,
            LmpCommon.Locks.LockType type,
            long nowMs,
            int minIntervalMs)
        {
            var key = (playerName, vesselId, type);
            if (lastEmitMs.TryGetValue(key, out var lastMs) && nowMs - lastMs < minIntervalMs)
                return false;
            lastEmitMs[key] = nowMs;
            return true;
        }

        public static void SendAllLocks(ClientStructure client)
        {
            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockListReplyMsgData>();
            msgData.Locks = LockSystem.LockQuery.GetAllLocks().ToArray();
            msgData.LocksCount = msgData.Locks.Length;

            MessageQueuer.SendToClient<LockSrvMsg>(client, msgData);
        }

        public static void ReleaseAndSendLockReleaseMessage(ClientStructure client, LockDefinition lockDefinition)
        {
            var lockReleaseResult = LockSystem.ReleaseLock(lockDefinition);
            if (lockReleaseResult)
            {
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockReleaseMsgData>();
                msgData.Lock = lockDefinition;
                msgData.LockResult = true;

                MessageQueuer.RelayMessage<LockSrvMsg>(client, msgData);
                LunaLog.Debug($"{lockDefinition.PlayerName} released lock {lockDefinition}");
            }
            else
            {
                SendStoredLockData(client, lockDefinition);
                LunaLog.Debug($"{lockDefinition.PlayerName} failed to release lock {lockDefinition}");
            }
        }

        public static void SendLockAcquireMessage(ClientStructure client, LockDefinition lockDefinition, bool force)
        {
            if (LockSystem.AcquireLock(lockDefinition, force, out var repeatedAcquire, client.Subspace))
            {
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockAcquireMsgData>();
                msgData.Lock = lockDefinition;
                msgData.Force = force;

                MessageQueuer.SendToAllClients<LockSrvMsg>(msgData);

                //Just log it if we actually changed the value. Users might send repeated acquire locks as they take a bit of time to reach them...
                if (!repeatedAcquire)
                    LunaLog.Debug($"{lockDefinition.PlayerName} acquired lock {lockDefinition}");
            }
            else
            {
                // [Stage 5.18d slice (c)] Emit a Reject message ONLY for the cross-
                // agency rejection path, and ONLY when the same (player, vessel,
                // type) key isn't still inside the debounce window. Other reject
                // reasons (cross-subspace past, vessel-not-in-store under gate=on,
                // existing-holder conflict) stay silent — same legacy UX.
                // SendStoredLockData below still runs in every reject case to
                // correct the client's view of the current lock state where
                // possible. Per the slice (c) consumer-lens review, the debounce
                // protects against toast spam from KSP's auto-acquire paths
                // (Tracking Station hover, physics-range entry update-lock
                // retake). Reject precedes SendStoredLockData so the toast
                // arrives before any holder-correction.
                if (LockSystem.IsCrossAgencyReject(lockDefinition, out var owningAgencyId))
                {
                    var nowMs = ServerContext.ServerClock.ElapsedMilliseconds;
                    if (ShouldEmitCrossAgencyReject(
                            CrossAgencyRejectLastEmitMs,
                            lockDefinition.PlayerName, lockDefinition.VesselId, lockDefinition.Type,
                            nowMs, CrossAgencyRejectMinIntervalMs))
                    {
                        var rejectMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<LockRejectMsgData>();
                        rejectMsg.Lock = lockDefinition;
                        rejectMsg.Reason = LockRejectReason.CrossAgency;
                        rejectMsg.OwningAgencyId = owningAgencyId;
                        MessageQueuer.SendToClient<LockSrvMsg>(client, rejectMsg);
                    }
                }

                SendStoredLockData(client, lockDefinition);
                LunaLog.Debug($"{lockDefinition.PlayerName} failed to acquire lock {lockDefinition}");
            }
        }

        /// <summary>
        /// Whenever a release/acquire lock fails, call this method to relay the correct lock definition to the player
        /// </summary>
        private static void SendStoredLockData(ClientStructure client, LockDefinition lockDefinition)
        {
            var storedLockDef = LockSystem.LockQuery.GetLock(lockDefinition.Type, lockDefinition.PlayerName, lockDefinition.VesselId, lockDefinition.KerbalName);
            if (storedLockDef != null)
            {
                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<LockAcquireMsgData>();
                msgData.Lock = storedLockDef;
                MessageQueuer.SendToClient<LockSrvMsg>(client, msgData);
            }
        }
    }
}
