using LmpClient.Base;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Stage 5.18c rename dialog for the local player's auto-registered agency.
    /// Opened from <see cref="AgencyWindow"/>'s "Rename agency..." button (or any
    /// future entry point that calls <see cref="OpenForRename"/>). Submits the
    /// trimmed name to the server via
    /// <see cref="AgencyMessageSender.SendCreateRequest"/>; the server's reply
    /// arrives asynchronously and updates <see cref="AgencySystem.LastCreateReplySuccess"/>
    /// + <see cref="AgencySystem.LastCreateReplyReason"/>, which the drawer renders
    /// inline as a success confirmation or rejection banner.
    ///
    /// <para><b>Server-side validation.</b> Trimmed/whitespace-only names, names
    /// over 64 chars, and names containing <c>=</c> / <c>{</c> / <c>}</c> /
    /// <c>\n</c> / <c>\r</c> / control chars are rejected by
    /// <c>AgencyMsgReader.ValidateDisplayName</c>. The window UX surfaces the
    /// server's rejection reason; client-side pre-validation is intentionally
    /// permissive (cap the TextField at 64 to match the server cap, but trust
    /// the server as the canonical validator — duplicating the regex client-side
    /// would drift). Empty-after-trim submits are no-op'd locally so the player
    /// doesn't burn a wire round on an obviously-bad input.</para>
    /// </summary>
    public partial class AgencyCreateWindow : Window<AgencyCreateWindow>
    {
        #region Fields

        // Same two-layer Display pattern as AgencyWindow (and AdminWindow): the
        // user-toggle (_display) survives a render that misses a transient
        // environmental condition, the setter routes through base.Display so
        // OnDisplay/OnHide stay in sync.
        // Display gate mirrors AgencyWindow's scene + handshake conditions —
        // a future entry point that toggles this window pre-SpaceCenter (or via
        // stale _display state during scene transition) would otherwise let the
        // Submit button fire a rename against a half-loaded session. The
        // StatusWindow's Agency toggle is already gated on its own scene check,
        // so the practical path is covered; this is defence-in-depth.
        // Career-mode is NOT gated here because the server's gate enforces that
        // (per spec §10 Q-Mode); a misconfigured server with PerAgencyCareer=on
        // under Sandbox/Science is the server's bug, not ours to silently mask.
        private bool _display;
        public override bool Display
        {
            get => base.Display && _display
                                && MainSystem.NetworkState >= ClientState.Running
                                && HighLogic.LoadedScene >= GameScenes.SPACECENTER
                                && SettingsSystem.ServerSettings.PerAgencyCareerEnabled
                                && AgencySystem.Singleton.LocalAgencyId != System.Guid.Empty;
            set => base.Display = _display = value;
        }

        // Pending input — kept across re-renders so the player doesn't lose typing
        // when the window momentarily blurs / re-shows from the same submit cycle.
        // Reset on OpenForRename so a stale value from a closed-and-reopened
        // session doesn't leak in.
        private static string _pendingDisplayName = string.Empty;
        private static GUIStyle _errorStyle;
        private static GUIStyle _successStyle;
        private static GUIStyle _hintStyle;
        private const int MaxDisplayNameLength = 64;

        private const float WindowHeight = 200;
        private const float WindowWidth = 360;

        private const string Title = "LMP - Rename agency";

        #endregion

        /// <summary>
        /// Opens the rename window pre-populated with the current display name so
        /// the player can edit rather than retype. Resets the LastCreateReply
        /// banner so a stale prior-rejection message from a previous session
        /// doesn't render on first open.
        /// </summary>
        public void OpenForRename()
        {
            // Reset the typed input to the canonical name so the player edits a
            // current value rather than retypes from scratch. This DOES discard
            // any in-progress typing from a prior open that didn't submit —
            // intentional, because the alternative (preserve _pendingDisplayName
            // across closes) leaves stale text the player no longer remembers
            // typing. If a player needs to preserve their draft, the path is
            // "submit it" rather than "close and reopen".
            _pendingDisplayName = AgencySystem.Singleton.LocalAgencyDisplayName ?? string.Empty;

            // Clear the prior server-reply state so the drawer banner re-arms
            // only after a fresh submit. Without this, a stale "Server rejected:
            // ..." banner from a previous attempt persists indefinitely across
            // close → reopen cycles (the OnDisabled clear at AgencySystem.cs
            // only covers the disconnect/reconnect case, not the in-session
            // close-and-reopen case both lens reviewers flagged). The success-
            // branch reply also stays around forever if the server ever ships a
            // non-empty Reason on Success; clearing here covers both.
            AgencySystem.Singleton.LastCreateReplySuccess = true;
            AgencySystem.Singleton.LastCreateReplyReason = string.Empty;

            Display = true;
        }

        protected override void OnCloseButton()
        {
            base.OnCloseButton();
            RemoveWindowLock();
            Display = false;
        }

        protected override void DrawGui()
        {
            WindowRect = FixWindowPos(GUILayout.Window(6727 + MainSystem.WindowOffset,
                WindowRect, DrawContent, Title, LayoutOptions));
        }

        public override void SetStyles()
        {
            WindowRect = new Rect(Screen.width / 2f - WindowWidth / 2f, Screen.height / 2f - WindowHeight / 2f, WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);

            LayoutOptions = new GUILayoutOption[4];
            LayoutOptions[0] = GUILayout.MinWidth(WindowWidth);
            LayoutOptions[1] = GUILayout.MaxWidth(WindowWidth);
            LayoutOptions[2] = GUILayout.MinHeight(WindowHeight);
            LayoutOptions[3] = GUILayout.MaxHeight(WindowHeight);

            _errorStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = XKCDColors.Red },
                wordWrap = true
            };
            _successStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Color.green },
                wordWrap = true
            };
            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Italic,
                fontSize = 11
            };
        }

        public override void RemoveWindowLock()
        {
            if (IsWindowLocked)
            {
                IsWindowLocked = false;
                InputLockManager.RemoveControlLock("LMP_AgencyCreateWindowLock");
            }
        }

        public override void CheckWindowLock()
        {
            if (Display)
            {
                if (MainSystem.NetworkState < ClientState.Running)
                {
                    RemoveWindowLock();
                    return;
                }

                Vector2 mousePos = Input.mousePosition;
                mousePos.y = Screen.height - mousePos.y;

                var shouldLock = WindowRect.Contains(mousePos);
                if (shouldLock && !IsWindowLocked)
                {
                    InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, "LMP_AgencyCreateWindowLock");
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
