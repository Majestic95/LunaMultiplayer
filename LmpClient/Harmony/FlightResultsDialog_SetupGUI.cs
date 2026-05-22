using HarmonyLib;
using KSP.UI.Dialogs;
using LmpClient.Localization;
using LmpClient.Systems.Lock;
using LmpClient.Systems.Revert;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using System;
using UnityEngine;
using UnityEngine.UI;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to disable the revert after switching vessels
    /// </summary>
    [HarmonyPatch(typeof(FlightResultsDialog))]
    [HarmonyPatch("SetupGUI")]
    public class FlightResultsDialog_SetupGUI
    {
        [HarmonyPostfix]
        private static void PostfixSetupGUI(Button ___Btn_revLaunch, Button ___Btn_revEditor)
        {
            if (MainSystem.NetworkState < ClientState.Connected) return;

            // [diag:revert] Mirror of the pause-menu instrumentation. The decision
            // matrix lives in [RevertGate]; AllowFreely + AllowWithConfirm both
            // leave the recovery buttons untouched (the FlightDriver_Revert*
            // prefixes handle soft-confirm), Block keeps the legacy disable.
            var activeVessel = FlightGlobals.ActiveVessel;
            var activeId = activeVessel ? activeVessel.id : Guid.Empty;
            var startingId = RevertSystem.Singleton.StartingVesselId;
            var spectating = VesselCommon.IsSpectating;
            var idMatch = activeVessel && activeId == startingId;
            var lockOwner = activeVessel ? LockSystem.LockQuery.GetControlLockOwner(activeId) : "";
            var decision = RevertGate.Decide(out var vesselName);
            LunaLog.Log($"[diag:revert] recovery-dialog decision={decision} vessel='{vesselName}' " +
                        $"active={activeId:N} starting={startingId:N} idMatch={idMatch} " +
                        $"spectating={spectating} controlLockOwner='{(string.IsNullOrEmpty(lockOwner) ? "(none)" : lockOwner)}'");

            if (decision != RevertDecision.Block)
                return;

            ___Btn_revLaunch.onClick.RemoveAllListeners();
            ___Btn_revEditor.onClick.RemoveAllListeners();

            ___Btn_revLaunch.onClick.AddListener(() =>
            {
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "CannotRevertLaunch",
                    LocalizationContainer.RevertDialogText.CannotRevertTitle,
                    LocalizationContainer.RevertDialogText.CannotRevertText,
                    LocalizationContainer.RevertDialogText.CloseBtn, false, HighLogic.UISkin);
            });

            ___Btn_revEditor.onClick.AddListener(() =>
            {
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "CannotRevertEditor",
                    LocalizationContainer.RevertDialogText.CannotRevertTitle,
                    LocalizationContainer.RevertDialogText.CannotRevertText,
                    LocalizationContainer.RevertDialogText.CloseBtn, false, HighLogic.UISkin);
            });
        }
    }
}
