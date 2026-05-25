using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceHostRefreshRpc
{
    private const byte RpcId = 206;

    public static void Send(int nonce)
    {
        var writer = StartWriter();
        if (writer == null) return;
        writer.Write(nonce);
        FinishWriter(writer);
    }

    private static MessageWriter? StartWriter()
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return null;
        return AmongUsClient.Instance.StartRpcImmediately(
            PlayerControl.LocalPlayer.NetId,
            RpcId,
            SendOption.Reliable,
            -1);
    }

    private static void FinishWriter(MessageWriter writer)
        => AmongUsClient.Instance.FinishRpcImmediately(writer);

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class PlayerControlHandleRpcPatch
    {
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != RpcId) return;

            try
            {
                var nonce = reader.ReadInt32();
                VoiceChatRoom.ApplyHostVoiceRefreshFromRpc(__instance, nonce);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("voice.refresh.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
