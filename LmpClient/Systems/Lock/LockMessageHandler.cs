using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Events;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using LmpCommon.Locks;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LmpClient.Systems.Lock
{
    public class LockMessageHandler : SubSystem<LockSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        private static readonly List<LockDefinition> LocksToRemove = new List<LockDefinition>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is LockBaseMsgData msgData)) return;

            switch (msgData.LockMessageType)
            {
                case LockMessageType.ListReply:
                    {
                        var data = (LockListReplyMsgData)msgData;
                        for (var i = 0; i < data.LocksCount; i++)
                        {
                            LockSystem.LockStore.AddOrUpdateLock(data.Locks[i]);
                        }

                        if (MainSystem.NetworkState < ClientState.LocksSynced)
                            MainSystem.NetworkState = ClientState.LocksSynced;
                    }
                    break;
                case LockMessageType.Acquire:
                    {
                        var data = (LockAcquireMsgData)msgData;
                        LockSystem.LockStore.AddOrUpdateLock(data.Lock);

                        LockEvent.onLockAcquire.Fire(data.Lock);
                    }
                    break;
                case LockMessageType.Release:
                    {
                        var data = (LockReleaseMsgData)msgData;
                        LockSystem.LockStore.RemoveLock(data.Lock);

                        LockEvent.onLockRelease.Fire(data.Lock);
                    }
                    break;
                case LockMessageType.Reject:
                    HandleReject((LockRejectMsgData)msgData);
                    break;
            }
        }

        /// <summary>
        /// Stage 5.18d slice (c) — surface the server's lock-acquire reject to
        /// the player via a <see cref="LunaScreenMsg"/> toast. Today only the
        /// <see cref="LockRejectReason.CrossAgency"/> reason is emitted on the
        /// wire (Stage 5.17a guard); other reject paths stay silent as they
        /// did pre-5.18d.
        ///
        /// <para>For the cross-agency case, resolves the owning agency's
        /// display name + owner from <see cref="AgencySystem.OtherAgencies"/>
        /// when known (typical case after handshake), falls back to a generic
        /// "a different agency" when the snapshot misses (late-joining peer
        /// whose AgencyInfo hasn't reached this client yet).</para>
        /// </summary>
        private static void HandleReject(LockRejectMsgData data)
        {
            // Only the cross-agency reason is currently surfaced. Future
            // reject reasons can append their own toast handling here without
            // breaking existing client builds — older clients fall into the
            // unknown-reason branch and log+ignore.
            if (data.Reason != LockRejectReason.CrossAgency)
            {
                LunaLog.LogWarning($"[Agency]: Received LockReject with unknown reason {data.Reason} — dropping.");
                return;
            }

            // Resolve the owning agency's friendly identity. The server
            // populated OwningAgencyId in the message; look it up in the
            // OtherAgencies snapshot for the display name. Falls back to a
            // generic literal when the snapshot misses (matches slice (h)'s
            // resolution pattern in VesselRemoveEvents.TryBlockCrossAgencyAction).
            var ownerLabel = "a different agency";
            var agencySystem = AgencySystem.Singleton;
            if (agencySystem != null
                && agencySystem.OtherAgencies != null
                && agencySystem.OtherAgencies.TryGetValue(data.OwningAgencyId, out var info)
                && info != null)
            {
                var displayName = string.IsNullOrEmpty(info.DisplayName) ? "an unnamed agency" : info.DisplayName;
                var owningPlayer = string.IsNullOrEmpty(info.OwningPlayerName) ? "unknown owner" : info.OwningPlayerName;
                ownerLabel = $"{displayName} ({owningPlayer})";
            }

            // Map LockType to a player-friendly verb so the toast reads
            // naturally. Control = "take control of"; Update / UnloadedUpdate
            // = "interact with" (consumer-lens v1 C1; "act on" was too vague
            // for non-Control auto-acquire failures). Update/UnloadedUpdate
            // fire from auto-acquire paths (physics range entry) that don't
            // map to a player UI click; "interact with" surfaces the vessel-
            // ownership constraint without misleading the player about what
            // action they took.
            var verb = data.Lock.Type == LockType.Control ? "take control of" : "interact with";

            LunaScreenMsg.PostScreenMessage(
                $"Cannot {verb} this vessel: it belongs to {ownerLabel}. " +
                "Ask the owning agency to give it to you, or ask a server admin to transfer it.",
                5f, ScreenMessageStyle.UPPER_CENTER);

            // Log-tag verb aligned with slice (h)'s cross-agency-{action}-blocked
            // grep convention (consumer-lens v1 S1) — `lock-blocked` instead of
            // `lock-rejected`. Includes local-player identity for centralised-
            // log triage (consumer-lens v1 S2; matches slice h precedent).
            var localPlayer = SettingsSystem.CurrentSettings?.PlayerName ?? "(unknown)";
            LunaLog.Log(
                $"[fix:per-agency-career] cross-agency-lock-blocked " +
                $"vessel={data.Lock.VesselId:N} type={data.Lock.Type} " +
                $"local-player={localPlayer} owning-agency={data.OwningAgencyId:N}");
        }
    }
}
