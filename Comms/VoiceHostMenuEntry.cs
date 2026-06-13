using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceHostMenuEntry
{
    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Start))]
    [HarmonyPostfix]
    static void PerfectComms_AddHostButton(GameSettingMenu __instance)
    {
        try
        {
            if (__instance == null) return;
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

            var preset = __instance.GameSettingsButton;
            var roles = __instance.RoleSettingsButton;
            var template = roles != null ? roles : preset;
            if (template == null) return;

            var clone = Object.Instantiate(template, template.transform.parent);
            clone.name = "PerfectCommsHostButton";
            Vector3 step = (preset != null && roles != null)
                ? roles.transform.localPosition - preset.transform.localPosition
                : new Vector3(0f, -0.55f, 0f);
            clone.transform.localPosition = template.transform.localPosition + step;

            foreach (var loc in clone.GetComponentsInChildren<TextTranslatorTMP>(true))
                if (loc != null) loc.enabled = false;

            clone.OnClick = new ButtonClickedEvent();
            clone.OnClick.AddListener((Action)(() =>
            {
                try { HostSettingsPanel.Show(); }
                catch (Exception e) { VoiceDiagnostics.DebugWarning($"[PerfectComms] Host button open failed: {e.Message}"); }
            }));
            clone.OnMouseOver ??= new UnityEvent();
            clone.OnMouseOut ??= new UnityEvent();

            clone.ChangeButtonText("Voice Settings");
        }
        catch (Exception e)
        {
            VoiceDiagnostics.DebugWarning($"[PerfectComms] Failed to add Host button: {e.Message}");
        }
    }

    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Close))]
    [HarmonyPostfix]
    static void PerfectComms_HideOnHostClose()
    {
        try { if (HostSettingsPanel.IsOpen) HostSettingsPanel.Hide(); }
        catch { }
    }
}
