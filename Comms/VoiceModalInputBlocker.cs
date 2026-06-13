using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceModalInputBlocker
{
    private static bool Blocked => VoiceUiKit.BlockGameInput;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickDown))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockClickDown() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickUp))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockClickUp() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveRepeatDown))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockRepeatDown() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickDownGraphic))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockClickDownGraphic() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveMouseOver))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockMouseOver() => !Blocked;
}
