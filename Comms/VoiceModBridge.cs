using PerfectComms.Api;

namespace VoiceChatPlugin.VoiceChat;

// Maps between the public PerfectComms.Api enums and the internal engine enums, in one place,
// so internal types stay free to change without touching the public API.
internal static class VoiceModBridge
{
    public static VoicePhaseKind ToApiPhase(VoiceGamePhase phase) => phase switch
    {
        VoiceGamePhase.Meeting => VoicePhaseKind.Meeting,
        VoiceGamePhase.Exile => VoicePhaseKind.Exile,
        VoiceGamePhase.Tasks => VoicePhaseKind.Tasks,
        _ => VoicePhaseKind.Lobby,
    };

    // ExternalVoiceState.ChannelShape (0/1/2) -> internal audio filter mode.
    public static VoiceAudioFilterMode ToFilterMode(int channelShape) => channelShape switch
    {
        (int)VoiceAudioShape.Muffle => VoiceAudioFilterMode.ListenerMuffle,
        (int)VoiceAudioShape.Proximity => VoiceAudioFilterMode.None,
        _ => VoiceAudioFilterMode.Radio,
    };
}
