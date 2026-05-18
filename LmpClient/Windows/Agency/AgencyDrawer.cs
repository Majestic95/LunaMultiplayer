using LmpClient.Systems.Agency;
using System.Globalization;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        protected override void DrawWindowContent(int windowId)
        {
            var agencySystem = AgencySystem.Singleton;

            GUILayout.BeginVertical();
            GUI.DragWindow(MoveRect);

            DrawLocalAgencySection(agencySystem);
            GUILayout.Space(8);
            DrawOtherAgenciesSection(agencySystem);

            GUILayout.EndVertical();
        }

        private static void DrawLocalAgencySection(AgencySystem agencySystem)
        {
            GUILayout.Label("Your agency", _sectionHeaderStyle);

            // Mid-handshake window: LocalAgencyId is set by AgencyHandshakeMsgData
            // but DisplayName/OwningPlayerName + the KSP-singleton scalars (which
            // AgencyStateMsgData drives via the SetXxxWithoutTriggeringEvent
            // helpers) arrive on the immediately-following State message. Bounded
            // to one Lidgren tick (5.18a XML) but a Window render landing inside
            // that gap would otherwise show "Your Agency" + Funds=0 / Sci=0 /
            // Rep=0 — looks like data loss. Treat empty DisplayName as the
            // canonical "State hasn't landed yet" signal and render a syncing
            // placeholder until then.
            var stateArrived = !string.IsNullOrEmpty(agencySystem.LocalAgencyDisplayName);

            if (!stateArrived)
            {
                GUILayout.Label("(syncing…)", _hintStyle);
                return;
            }

            GUILayout.Label($"Name: {agencySystem.LocalAgencyDisplayName}");

            if (!string.IsNullOrEmpty(agencySystem.LocalAgencyOwningPlayerName))
                GUILayout.Label($"Player: {agencySystem.LocalAgencyOwningPlayerName}");

            // Career scalars are read directly from KSP's authoritative singletons.
            // The server's AgencyScenarioProjector populates these per-agency at
            // scene-load and the owner-only AgencyStateMsgData echoes keep them
            // current mid-session. Defensive null-checks cover the brief windows
            // before scene-load (Funding.Instance et al. are null in the main menu;
            // the Display gate keeps us out of those, but a scene transition could
            // race the render).
            var funds = Funding.Instance?.Funds ?? 0;
            var science = ResearchAndDevelopment.Instance?.Science ?? 0;
            var reputation = Reputation.Instance?.reputation ?? 0;

            GUILayout.Label($"Funds: {funds.ToString("N0", CultureInfo.InvariantCulture)}");
            GUILayout.Label($"Science: {science.ToString("N0", CultureInfo.InvariantCulture)}");
            GUILayout.Label($"Reputation: {reputation.ToString("N0", CultureInfo.InvariantCulture)}");
            GUILayout.Label($"Vessels: {CountOwnedVessels(agencySystem, agencySystem.LocalAgencyId)}");

            GUILayout.Space(4);

            if (AgencyLabelFormatter.IsDefaultDisplayName(
                    agencySystem.LocalAgencyDisplayName,
                    agencySystem.LocalAgencyOwningPlayerName))
            {
                // Informational, not action-prompting — players upgrading from a
                // pre-0.31 universe per spec §9 will see this hint on first login
                // for every player who hasn't customized, including players who
                // are happy with the default. Avoid nagging language.
                GUILayout.Label("Default agency name (auto-generated from your player name).", _hintStyle);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rename agency..."))
            {
                AgencyCreateWindow.Singleton.OpenForRename();
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawOtherAgenciesSection(AgencySystem agencySystem)
        {
            GUILayout.Label($"Other agencies ({agencySystem.OtherAgencies.Count})", _sectionHeaderStyle);

            if (agencySystem.OtherAgencies.Count == 0)
            {
                GUILayout.Label("No other agencies on this server yet.");
                return;
            }

            _otherAgenciesScroll = GUILayout.BeginScrollView(_otherAgenciesScroll, GUILayout.ExpandHeight(true));
            foreach (var info in agencySystem.OtherAgencies.Values)
            {
                if (info == null) continue;
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(string.IsNullOrEmpty(info.DisplayName)
                    ? AgencyLabelFormatter.UnknownAgencyLabel
                    : info.DisplayName);
                if (!string.IsNullOrEmpty(info.OwningPlayerName))
                    GUILayout.Label($"Player: {info.OwningPlayerName}");
                GUILayout.Label($"Vessels: {CountOwnedVessels(agencySystem, info.AgencyId)}");
                GUILayout.Label("Funds / Science / Reputation: private");
                GUILayout.EndVertical();
            }

            // Honest disclosure of the 5.18a snapshot gap: OtherAgencies is one-
            // shot at this client's handshake time, so any agency that came
            // online AFTER doesn't appear in the list — but the vessels they
            // own ARE in VesselOwnership (delivered via VesselProto). Without
            // this line, those vessels are silently uncounted in the
            // per-agency breakdown above. Closes a small consumer-lens
            // observation; goes away when 5.18d's AgencyVisibilityMsgData
            // delivers incremental visibility updates.
            var unknownVessels = CountUnknownOwnerVessels(agencySystem);
            if (unknownVessels > 0)
            {
                GUILayout.Label($"Vessels owned by agencies not yet known: {unknownVessels}", _hintStyle);
            }

            GUILayout.EndScrollView();
        }

        private static int CountOwnedVessels(AgencySystem agencySystem, System.Guid agencyId)
        {
            // ConcurrentDictionary enumeration is snapshot-style (safe under
            // concurrent writes from the wire path). Count is small (vessel count
            // per server is typically <100 even on large servers), so a per-render
            // iteration is fine. If this ever becomes hot, cache per-frame.
            var count = 0;
            foreach (var kvp in agencySystem.VesselOwnership)
            {
                if (kvp.Value == agencyId) count++;
            }
            return count;
        }

        private static int CountUnknownOwnerVessels(AgencySystem agencySystem)
        {
            // Owning agency is non-Empty (claimed) but absent from OtherAgencies +
            // not the local agency = a late-joiner agency this client doesn't know
            // about yet. Excludes Empty (Unassigned sentinel — counted elsewhere
            // if we add that breakdown, but irrelevant here) and the local
            // player's own vessels.
            var count = 0;
            foreach (var kvp in agencySystem.VesselOwnership)
            {
                if (kvp.Value == System.Guid.Empty) continue;
                if (kvp.Value == agencySystem.LocalAgencyId) continue;
                if (!agencySystem.OtherAgencies.ContainsKey(kvp.Value)) count++;
            }
            return count;
        }
    }
}
