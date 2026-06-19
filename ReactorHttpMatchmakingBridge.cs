using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using static UnityEngine.UI.Button;

namespace VoiceChatPlugin;

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
internal static class GameStartManagerModdedRegionPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(GameStartManager __instance)
    {
        if (!ReactorHttpMatchmakingBridge.IsKnownModdedRegion()) return;

        ReactorHttpMatchmakingBridge.RestorePublicToggle(__instance);
    }
}

internal static class ReactorHttpMatchmakingBridge
{
    private static readonly FieldInfo? HostPublicButtonField = ResolveField("HostPublicButton");
    private static readonly FieldInfo? HostPrivateButtonField = ResolveField("HostPrivateButton");
    private static string? _lastWarning;

    private static FieldInfo? ResolveField(string name)
    {
        var field = AccessTools.Field(typeof(GameStartManager), name);
        if (field == null)
            WarnOnce($"GameStartManager.{name} not found (game update?); public-lobby toggle disabled");
        return field;
    }

    internal static bool IsKnownModdedRegion()
    {
        try
        {
            var region = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion;
            if (region == null) return false;

            if (IsKnownModdedValue(region.Name)
                || IsKnownModdedValue(region.PingServer)
                || IsKnownModdedValue(region.TargetServer))
                return true;

            var servers = region.Servers;
            if (servers == null) return false;
            foreach (var server in servers)
            {
                if (server == null) continue;
                if (IsKnownModdedValue(server.Name)
                    || IsKnownModdedValue(server.Ip)
                    || IsKnownModdedValue(server.HttpUrl))
                    return true;
            }
        }
        catch (Exception ex)
        {
            WarnOnce($"region check failed: {ex.Message}");
        }

        return false;
    }

    internal static void RestorePublicToggle(GameStartManager manager)
    {
        try
        {
            if (HostPublicButtonField?.GetValue(manager) is PassiveButton publicButton)
                publicButton.enabled = true;

            if (HostPrivateButtonField?.GetValue(manager) is not PassiveButton privateButton) return;

            privateButton.enabled = true;
            privateButton.OnClick = new ButtonClickedEvent();
            privateButton.OnClick.AddListener((Action)manager.MakePublic);

            var inactive = privateButton.transform.FindChild("Inactive");
            if (inactive != null && inactive.GetComponent<SpriteRenderer>() is { } sprite)
                sprite.color = Color.white;
        }
        catch (Exception ex)
        {
            WarnOnce($"public toggle restore failed: {ex.Message}");
        }
    }

    private static bool IsKnownModdedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("duikbo.at", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("aumods.org", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("modded", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void WarnOnce(string warning)
    {
        if (string.Equals(_lastWarning, warning, StringComparison.Ordinal)) return;
        _lastWarning = warning;
        VoiceDiagnostics.DebugWarning($"[VC] {warning}");
    }
}
