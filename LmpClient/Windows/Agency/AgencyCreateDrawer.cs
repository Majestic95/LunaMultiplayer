using LmpClient.Systems.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyCreateWindow
    {
        protected override void DrawWindowContent(int windowId)
        {
            var agencySystem = AgencySystem.Singleton;

            GUILayout.BeginVertical();
            GUI.DragWindow(MoveRect);

            GUILayout.Label("Current name:");
            GUILayout.Label(string.IsNullOrEmpty(agencySystem.LocalAgencyDisplayName)
                ? AgencyLabelFormatter.LocalFallbackLabel
                : agencySystem.LocalAgencyDisplayName);

            GUILayout.Space(6);
            GUILayout.Label("New name (up to 64 characters):");
            _pendingDisplayName = GUILayout.TextField(
                _pendingDisplayName ?? string.Empty,
                MaxDisplayNameLength,
                GUILayout.Width(WindowWidth - 30));
            GUILayout.Label("No newlines, '=', or '{', '}' characters.", _hintStyle);

            GUILayout.Space(6);

            // Server-supplied reply banner. The handler updates these whenever
            // an AgencyCreateReplyMsgData arrives, including the success path
            // (which also flips LocalAgencyDisplayName). We render the banner
            // either way so the player gets a confirmation tick on success and
            // a rejection reason on failure.
            if (!agencySystem.LastCreateReplySuccess && !string.IsNullOrEmpty(agencySystem.LastCreateReplyReason))
            {
                GUILayout.Label($"Server rejected: {agencySystem.LastCreateReplyReason}", _errorStyle);
            }
            else if (agencySystem.LastCreateReplySuccess && !string.IsNullOrEmpty(agencySystem.LastCreateReplyReason))
            {
                GUILayout.Label(agencySystem.LastCreateReplyReason, _successStyle);
            }

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            GUI.enabled = !string.IsNullOrWhiteSpace(_pendingDisplayName);
            if (GUILayout.Button("Submit"))
            {
                // Trim client-side so accidental leading/trailing whitespace
                // doesn't cause server rejection; server also trims, but the
                // client trim keeps the UX snappy on the same-name no-op case.
                AgencySystem.Singleton.MessageSender.SendCreateRequest(_pendingDisplayName.Trim());
            }
            GUI.enabled = true;
            if (GUILayout.Button("Cancel"))
            {
                Display = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }
    }
}
