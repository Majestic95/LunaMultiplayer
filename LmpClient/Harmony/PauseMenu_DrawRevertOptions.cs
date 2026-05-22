using HarmonyLib;
using LmpClient.Localization;
using LmpClient.Systems.Lock;
using LmpClient.Systems.Revert;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using System;
using System.Collections.Generic;
// Decision/Confirm types live in LmpClient.Systems.Revert (RevertGate.cs); the
// Revert namespace is already imported above for RevertSystem.

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to disable the revert after switching vessels
    /// </summary>
    [HarmonyPatch(typeof(PauseMenu))]
    [HarmonyPatch("drawStockRevertOptions")]
    public class PauseMenu_DrawRevertOptions
    {
        [HarmonyPostfix]
        private static void PostfixDrawStockRevertOptions(PopupDialog dialog, List<DialogGUIBase> options)
        {
            if (MainSystem.NetworkState < ClientState.Connected) return;

            // [diag:revert] Capture full gate state on every pause-menu open.
            // Cadence is one line per ESC press — low noise. The decision feeds
            // into both the UI (this Postfix) and the FlightDriver_Revert* confirm
            // intercept; logging here gives us the player-visible action point.
            var activeVessel = FlightGlobals.ActiveVessel;
            var activeId = activeVessel ? activeVessel.id : Guid.Empty;
            var startingId = RevertSystem.Singleton.StartingVesselId;
            var spectating = VesselCommon.IsSpectating;
            var idMatch = activeVessel && activeId == startingId;
            var lockOwner = activeVessel ? LockSystem.LockQuery.GetControlLockOwner(activeId) : "";
            var decision = RevertGate.Decide(out var vesselName);
            LunaLog.Log($"[diag:revert] pause-menu decision={decision} vessel='{vesselName}' " +
                        $"active={activeId:N} starting={startingId:N} idMatch={idMatch} " +
                        $"spectating={spectating} controlLockOwner='{(string.IsNullOrEmpty(lockOwner) ? "(none)" : lockOwner)}'");

            // AllowFreely and AllowWithConfirm both leave KSP's native revert
            // buttons untouched — the FlightDriver_Revert* prefixes handle the
            // soft-confirm dialog at the actual revert call site, so the player
            // sees stock KSP UI on the way in and our dialog only when needed.
            if (decision != RevertDecision.Block)
                return;

            foreach (var guiComponent in options)
            {
                if (guiComponent is DialogGUILabel guiLabel)
                {
                    guiLabel.OptionText = LocalizationContainer.RevertDialogText.CannotRevertText;
                }

                if (guiComponent is DialogGUIButton guiButton)
                {
                    guiButton.OptionInteractableCondition = () => false;
                }
            }
        }
    }
}
