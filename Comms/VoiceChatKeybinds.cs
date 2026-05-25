using MiraAPI.Keybinds;
using Rewired;

namespace VoiceChatPlugin.VoiceChat;

[RegisterCustomKeybinds]
public static class VoiceChatKeybinds
{
    public static MiraKeybind ToggleMute { get; } =
        new("Mute / Unmute Mic", KeyboardKeyCode.M);

    public static MiraKeybind ImpostorRadio { get; } =
        new("Impostor Radio (Hold)", KeyboardKeyCode.V);

    public static MiraKeybind PushToTalk { get; } =
        new("Push To Talk (Hold)", KeyboardKeyCode.C);

    public static MiraKeybind ToggleSpeaker { get; } =
        new("Toggle Speaker", KeyboardKeyCode.N);

    public static MiraKeybind VolumeMenu { get; } =
        new("Player Volumes", KeyboardKeyCode.B);

    public static MiraKeybind LocalVoiceRefresh { get; } =
        new("Refresh Voice Connection", KeyboardKeyCode.F7);

    public static MiraKeybind HostVoiceRefresh { get; } =
        new("Refresh Voice Connections (Host)", KeyboardKeyCode.F8);
}
