using System.Collections.Concurrent;
using System.Linq;
using KSP.UI.Screens;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Systems.ShareScience;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;

namespace LmpClient.Systems.ShareTechnology
{
    public class ShareTechnologyMessageHandler : SubSystem<ShareTechnologySystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is ShareProgressBaseMsgData msgData)) return;

            switch (msgData.ShareProgressMessageType)
            {
                case ShareProgressMessageType.TechnologyUpdate:
                    if (msgData is ShareProgressTechnologyMsgData techMsg)
                    {
                        var tech = new TechNodeInfo(techMsg.TechNode); //create a copy of the tech value so it will not change in the future.
                        LunaLog.Log($"Queue TechnologyResearch with: {tech.Id}");
                        System.QueueAction(() => TechnologyResearch(tech));
                    }
                    return;

                case ShareProgressMessageType.TechnologyRejected:
                    if (msgData is ShareProgressTechnologyRejectedMsgData rejection)
                    {
                        // [fix:BUG-025] Server saw the tech was already unlocked; refund the
                        // science we deducted locally before broadcasting. Refund routes through
                        // KSP's normal AddScience path so the HUD/audit log show the transaction.
                        var techId = rejection.TechId;
                        var refund = rejection.RefundScience;
                        LunaLog.Log($"[fix:BUG-025] Server rejected duplicate purchase of {techId}; refunding {refund} science");
                        System.QueueAction(() => RefundScience(techId, refund));
                    }
                    return;

                default:
                    return;
            }
        }

        private static void TechnologyResearch(TechNodeInfo tech)
        {
            System.StartIgnoringEvents();
            var node = AssetBase.RnDTechTree.GetTreeTechs().ToList().Find(n => n.techID == tech.Id);

            //Unlock the technology
            ResearchAndDevelopment.Instance.UnlockProtoTechNode(node);

            //Refresh the tech tree
            ResearchAndDevelopment.RefreshTechTreeUI();

            //Refresh the part list in case we are in the VAB/SPH
            if (EditorPartList.Instance) EditorPartList.Instance.Refresh();

            System.StopIgnoringEvents();
            LunaLog.Log($"TechnologyResearch received - technology researched: {tech.Id}");
        }

        private static void RefundScience(string techId, float refund)
        {
            if (refund <= 0f || ResearchAndDevelopment.Instance == null) return;

            // [fix:BUG-025] AddScience fires GameEvents.OnScienceChanged. ShareScienceSystem
            // subscribes to that event and would broadcast our new science total back to the
            // server, where it fans out to every peer — overwriting their science with ours.
            // Suppress ShareScienceSystem's listener for the duration of the refund call
            // (matches the StartIgnoringEvents pattern used by other receive-path handlers).
            ShareScienceSystem.Singleton.StartIgnoringEvents();
            try
            {
                ResearchAndDevelopment.Instance.AddScience(refund, TransactionReasons.RnDTechResearch);
            }
            finally
            {
                ShareScienceSystem.Singleton.StopIgnoringEvents();
            }
            LunaLog.Log($"[fix:BUG-025] Refunded {refund} science for rejected duplicate purchase of {techId}");
        }
    }
}
