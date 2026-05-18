using LmpClient.Base;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Stage 5.18c per-agency career UI. Read-only window showing the local player's
    /// agency (DisplayName + Funds/Science/Reputation read directly from KSP's
    /// authoritative singletons, populated by the server's
    /// <c>AgencyScenarioProjector</c> at scene-load + the owner-only
    /// <c>AgencyStateMsgData</c> mid-session echoes) plus a public summary of the
    /// other agencies on the server (id + owner + display name + vessel count —
    /// scalars are private per spec §10 Q1 <c>PrivateAgencyResources=true</c>).
    ///
    /// <para><b>Gating.</b> Displays only when (a) the server has
    /// <c>PerAgencyCareerEnabled=true</c> (dual-mode silence in shared-agency mode),
    /// (b) the local handshake has completed and assigned an agency id, (c) we're
    /// in or past the Space Center scene (UI surfaces don't make sense at the main
    /// menu), and (d) we're in Career game mode (per spec §10 Q-Mode — per-agency
    /// is career-only because Science/Sandbox don't have a <c>Funding.Instance</c>
    /// to project to).</para>
    ///
    /// <para>Toggle visibility from the status-window agency button (also gated on
    /// PerAgencyCareerEnabled). The rename UX lives in the sibling
    /// <see cref="AgencyCreateWindow"/>; clicking the rename button here opens that
    /// window.</para>
    /// </summary>
    public partial class AgencyWindow : Window<AgencyWindow>
    {
        #region Fields

        // Two-layer Display pattern (matching AdminWindow.cs):
        //   base.Display gates on disclaimer + the base _display field (driven by
        //     OnDisplay/OnHide events for any styling reactions).
        //   our _display is the user-toggle from the status-window agency button.
        //   the remaining conditions are environmental: network running, post-
        //     SPACECENTER scene, server in per-agency mode, handshake done,
        //     game-mode is Career (spec §10 Q-Mode — per-agency is career-only).
        // The setter propagates to base so OnDisplay/OnHide stay in sync with the
        // toggle, and to our _display so the user's toggle survives a Display
        // re-render that misses one of the environmental conditions.
        private bool _display;
        public override bool Display
        {
            get => base.Display && _display
                                && MainSystem.NetworkState >= ClientState.Running
                                && HighLogic.LoadedScene >= GameScenes.SPACECENTER
                                && SettingsSystem.ServerSettings.PerAgencyCareerEnabled
                                && AgencySystem.Singleton.LocalAgencyId != System.Guid.Empty
                                && HighLogic.CurrentGame?.Mode == Game.Modes.CAREER;
            set => base.Display = _display = value;
        }

        private static Vector2 _otherAgenciesScroll;
        private static GUIStyle _sectionHeaderStyle;
        private static GUIStyle _hintStyle;

        private const float WindowHeight = 380;
        private const float WindowWidth = 320;

        private const string Title = "LMP - Agency";

        #endregion

        protected override void OnCloseButton()
        {
            base.OnCloseButton();
            RemoveWindowLock();
            Display = false;
        }

        protected override void DrawGui()
        {
            WindowRect = FixWindowPos(GUILayout.Window(6726 + MainSystem.WindowOffset,
                WindowRect, DrawContent, Title, LayoutOptions));
        }

        public override void SetStyles()
        {
            WindowRect = new Rect(Screen.width * 0.9f - WindowWidth - 10, Screen.height / 2f - WindowHeight / 2f, WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);

            LayoutOptions = new GUILayoutOption[4];
            LayoutOptions[0] = GUILayout.MinWidth(WindowWidth);
            LayoutOptions[1] = GUILayout.MaxWidth(WindowWidth);
            LayoutOptions[2] = GUILayout.MinHeight(WindowHeight);
            LayoutOptions[3] = GUILayout.MaxHeight(WindowHeight);

            _sectionHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                stretchWidth = true
            };

            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Italic,
                fontSize = 11,
                normal = { textColor = XKCDColors.KSPNotSoGoodOrange }
            };
        }

        public override void RemoveWindowLock()
        {
            if (IsWindowLocked)
            {
                IsWindowLocked = false;
                InputLockManager.RemoveControlLock("LMP_AgencyWindowLock");
            }
        }

        public override void CheckWindowLock()
        {
            if (Display)
            {
                if (MainSystem.NetworkState < ClientState.Running || HighLogic.LoadedSceneIsFlight)
                {
                    RemoveWindowLock();
                    return;
                }

                Vector2 mousePos = Input.mousePosition;
                mousePos.y = Screen.height - mousePos.y;

                var shouldLock = WindowRect.Contains(mousePos);
                if (shouldLock && !IsWindowLocked)
                {
                    InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, "LMP_AgencyWindowLock");
                    IsWindowLocked = true;
                }
                if (!shouldLock && IsWindowLocked)
                    RemoveWindowLock();
            }

            if (!Display && IsWindowLocked)
                RemoveWindowLock();
        }
    }
}
