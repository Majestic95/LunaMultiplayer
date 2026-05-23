using System.Collections.Concurrent;
using System.Text;
using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SafetyBubble;
using LmpClient.Systems.SettingsSys;
using LmpClient.VesselUtilities;
using LmpCommon;
using LmpCommon.Enums;

namespace LmpClient.Systems.Status
{
    public class StatusSystem : MessageSystem<StatusSystem, StatusMessageSender, StatusMessageHandler>
    {
        #region Fields

        public PlayerStatus MyPlayerStatus { get; } = new PlayerStatus();

        public ConcurrentDictionary<string, PlayerStatus> PlayerStatusList { get; } = new ConcurrentDictionary<string, PlayerStatus>();

        private PlayerStatus LastPlayerStatus { get; } = new PlayerStatus();

        private bool StatusIsDifferent =>
            MyPlayerStatus.VesselText != LastPlayerStatus.VesselText ||
            MyPlayerStatus.StatusText != LastPlayerStatus.StatusText ||
            //Phase 1 of server-side-offload — Scene change must trigger a SendOwnStatus
            //so the server's relay filter (MessageQueuer.RelayMessageToFlightScene)
            //sees the new scene within ~1s of transition. The existing 1000ms
            //CheckPlayerStatus routine drives the send via the same StatusIsDifferent
            //gate, so no new event-hook is needed; just including Scene here covers
            //transitions in either direction (Flight↔SC, Flight→Editor, etc.).
            MyPlayerStatus.Scene != LastPlayerStatus.Scene;

        private static readonly StringBuilder StrBuilder = new StringBuilder();

        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(StatusSystem);

        protected override bool ProcessMessagesInUnityThread => false;

        protected override void OnEnabled()
        {
            base.OnEnabled();

            MyPlayerStatus.PlayerName = SettingsSystem.CurrentSettings.PlayerName;
            MyPlayerStatus.StatusText = LastPlayerStatus.StatusText = StatusTexts.Syncing;
            MyPlayerStatus.VesselText = LastPlayerStatus.VesselText = string.Empty;
            //Phase 1 of server-side-offload — initialize Scene BEFORE the first
            //SendOwnStatus below so the server sees the joining client's true scene
            //immediately instead of Unknown-for-one-second. Without this, the joining
            //client gets full-relay-always for ~1s after handshake (compat path) then
            //the next CheckPlayerStatus tick fires the real scene. Functionally
            //harmless but wastes one tick of bandwidth on the join.
            MyPlayerStatus.Scene = LastPlayerStatus.Scene = MapKspSceneToClientSceneType(HighLogic.LoadedScene);

            MessageSender.SendOwnStatus();

            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, CheckPlayerStatus));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            PlayerStatusList.Clear();
        }

        #endregion

        #region Public methods

        public int GetPlayerCount()
        {
            return PlayerStatusList.Count;
        }

        public PlayerStatus GetPlayerStatus(string playerName)
        {
            if (playerName == SettingsSystem.CurrentSettings.PlayerName)
                return MyPlayerStatus;

            return PlayerStatusList.ContainsKey(playerName) ? PlayerStatusList[playerName] : null;
        }

        public void RemovePlayer(string playerToRemove)
        {
            if (PlayerStatusList.ContainsKey(playerToRemove))
            {
                PlayerStatusList.TryRemove(playerToRemove, out _);
            }
        }

        #endregion

        #region Update methods

        private void CheckPlayerStatus()
        {
            if (!Enabled) return;

            //Phase 1 of server-side-offload — track Scene regardless of LoadedScene
            //so transitions to/from Editor / MainMenu also send a PlayerStatusSet
            //(needed so the server can filter relays to that scene's recipients).
            //Vessel/Status text is still only refreshed in the SC..TS range — those
            //getters dereference FlightGlobals.ActiveVessel which is only valid in
            //Flight, and the existing UI consumers only read VesselText/StatusText
            //in SC..TS anyway.
            MyPlayerStatus.Scene = MapKspSceneToClientSceneType(HighLogic.LoadedScene);

            if (HighLogic.LoadedScene >= GameScenes.SPACECENTER && HighLogic.LoadedScene <= GameScenes.TRACKSTATION)
            {
                MyPlayerStatus.VesselText = GetVesselText();
                MyPlayerStatus.StatusText = GetStatusText();
            }

            if (StatusIsDifferent)
            {
                LastPlayerStatus.VesselText = MyPlayerStatus.VesselText;
                LastPlayerStatus.StatusText = MyPlayerStatus.StatusText;
                LastPlayerStatus.Scene = MyPlayerStatus.Scene;

                MessageSender.SendOwnStatus();
            }
        }

        /// <summary>
        /// Phase 1 of server-side-offload — pure mapping from KSP's GameScenes enum
        /// to the wire-shape <see cref="ClientSceneType"/>. Drives the server's
        /// scene-aware relay filter. No R&D scene exists in stock KSP (it's a UI
        /// child of SPACECENTER), so ResearchAndDevelopment in the enum is
        /// future-proofing for mods that may introduce scene-like UIs.
        /// </summary>
        internal static ClientSceneType MapKspSceneToClientSceneType(GameScenes scene)
        {
            switch (scene)
            {
                case GameScenes.MAINMENU:
                case GameScenes.SETTINGS:
                case GameScenes.CREDITS:
                    return ClientSceneType.MainMenu;
                case GameScenes.SPACECENTER:
                    return ClientSceneType.SpaceCenter;
                case GameScenes.TRACKSTATION:
                    return ClientSceneType.TrackingStation;
                case GameScenes.EDITOR:
                    return ClientSceneType.Editor;
                case GameScenes.FLIGHT:
                    return ClientSceneType.Flight;
                case GameScenes.MISSIONBUILDER:
                    return ClientSceneType.Mission;
                default:
                    //LOADING / LOADINGBUFFER / PSYSTEM — not user-visible scenes.
                    //Falls into Other; server treats Other as "not Flight/TS" → drops
                    //continuous vessel-state relays. Recipients spending real time in
                    //LOADING are between scenes anyway (sub-second usually) and
                    //wouldn't render the relays.
                    return ClientSceneType.Other;
            }
        }

        #endregion

        #region Status getter

        private static string GetVesselText()
        {
            return !VesselCommon.IsSpectating && FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.vesselName
                : string.Empty;
        }

        private static string GetCurrentShipStatus()
        {
            if (SafetyBubbleSystem.Singleton.IsInSafetyBubble(FlightGlobals.ActiveVessel))
                return StatusTexts.InsideSafetyBubble;

            StrBuilder.Length = 0;
            switch (FlightGlobals.ActiveVessel.situation)
            {
                case Vessel.Situations.DOCKED:
                    return StrBuilder.Append(StatusTexts.Docked).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                case Vessel.Situations.ESCAPING:
                    if (FlightGlobals.ActiveVessel.orbit.timeToPe < 0)
                        return StrBuilder.Append(StatusTexts.Escaping).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                    return StrBuilder.Append(StatusTexts.Encountering).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                case Vessel.Situations.FLYING:
                    return StrBuilder.Append(StatusTexts.Flying).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                case Vessel.Situations.LANDED:
                    return StrBuilder.Append(StatusTexts.Landed).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                case Vessel.Situations.ORBITING:
                    return StrBuilder.Append(StatusTexts.Orbiting).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                case Vessel.Situations.PRELAUNCH:
                    return StrBuilder.Append(StatusTexts.Launching).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                case Vessel.Situations.SPLASHED:
                    return StrBuilder.Append(StatusTexts.Splashed).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                case Vessel.Situations.SUB_ORBITAL:
                    if (FlightGlobals.ActiveVessel.verticalSpeed > 0)
                        return StrBuilder.Append(StatusTexts.Ascending).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                    return StrBuilder.Append(StatusTexts.Descending).Append(' ').Append(FlightGlobals.ActiveVessel.mainBody.bodyName).ToString();
                default:
                    return StatusTexts.Error;
            }
        }

        private static string GetSpectatingShipStatus()
        {
            if (LockSystem.LockQuery.ControlLockBelongsToPlayer(FlightGlobals.ActiveVessel.id, SettingsSystem.CurrentSettings.PlayerName))
                return StatusTexts.WaitingControl;

            StrBuilder.Length = 0;
            return StrBuilder.Append(StatusTexts.Spectating).Append(' ').Append(LockSystem.LockQuery.GetControlLockOwner(FlightGlobals.ActiveVessel.id)).ToString();
        }

        private string GetStatusText()
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.FLIGHT:
                    if (FlightGlobals.ActiveVessel != null)
                        return !VesselCommon.IsSpectating ? GetCurrentShipStatus() : GetSpectatingShipStatus();
                    break;
                case GameScenes.EDITOR:
                    switch (EditorDriver.editorFacility)
                    {
                        case EditorFacility.VAB:
                            return StatusTexts.BuildingVab;
                        case EditorFacility.SPH:
                            return StatusTexts.BuildingSph;
                    }
                    break;
                case GameScenes.SPACECENTER:
                    return StatusTexts.SpaceCenter;
                case GameScenes.TRACKSTATION:
                    return StatusTexts.TrackStation;
            }

            return MyPlayerStatus.StatusText;
        }

        #endregion
    }
}
