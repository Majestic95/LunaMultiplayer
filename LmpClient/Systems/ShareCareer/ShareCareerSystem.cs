using Contracts;
using LmpClient.Base;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LmpClient.Systems.ShareCareer
{
    /// <summary>
    /// Class for holding a queue of career actions that will be processed in the right order.
    /// The Systems ShareContracts, ShareAchievements and ShareReputation will use this queue instead of their own
    /// to keep the right order of execution.
    /// </summary>
    public class ShareCareerSystem : System<ShareCareerSystem>
    {
        public override string SystemName { get; } = nameof(ShareCareerSystem);

        // Eager init so QueueAction is safe before OnEnabled fires. AgencyMessageHandler
        // (v5) routes through here as soon as ClientState.Handshaked traffic arrives on
        // channel 22, which is BEFORE this system's default-Running EnableStage. Pre-
        // Running QueueAction lands here, RunQueue's synchronous call bails on
        // ShareSystemReady=false (no SpaceCenter scene yet), and the action waits in
        // the queue until OnEnabled's 1Hz routine picks it up. Queue persists across
        // Enabled state to preserve any pre-enable enqueues; OnDisabled clears it so a
        // reconnect doesn't replay stale actions from the previous session.
        private readonly Queue<Action> _actionQueue = new Queue<Action>();

        //Dependencies to run the queue
        protected bool ShareSystemReady => ContractSystem.Instance != null && Funding.Instance != null &&
                                           ResearchAndDevelopment.Instance != null && Reputation.Instance != null &&
                                           Time.timeSinceLevelLoad > 1f && HighLogic.LoadedScene >= GameScenes.SPACECENTER && HighLogic.LoadedScene <= GameScenes.TRACKSTATION;

        protected override void OnEnabled()
        {
            if (SettingsSystem.ServerSettings.GameMode != GameMode.Career) return;

            base.OnEnabled();

            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, RunQueue));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            _actionQueue.Clear();
        }

        /// <summary>
        /// Queue an action that is dependent on the ActionDependency and will run
        /// if the ActionDependencyReady method returns true. For example a action like:
        /// Funding.Instance.SetFunds(1000, TransactionReasons.None);
        /// </summary>
        /// <param name="action"></param>
        public void QueueAction(Action action)
        {
            _actionQueue.Enqueue(action);
            RunQueue();
        }

        /// <summary>
        /// Run the queue and call the actions.
        /// </summary>
        private void RunQueue()
        {
            while (_actionQueue.Count > 0 && ShareSystemReady)
            {
                var action = _actionQueue.Dequeue();
                action?.Invoke();
            }
        }

    }
}
