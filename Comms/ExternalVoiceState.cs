using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// All third-party mod voice state for one player, resolved once per snapshot build by
// VoiceModRegistry from registered PerfectComms.Api callbacks. Bundling every external field
// here keeps the mod API fully isolated from the core VoicePlayerSnapshot shape: the engine
// reads plain resolved values, never a mod callback. Default = neutral (no external effect).
//
// ChannelShape: 0 = Proximity, 1 = Radio, 2 = Muffle (mirrors PerfectComms.Api.VoiceAudioShape).
internal readonly record struct ExternalVoiceState(
    // Gate (Primitive 1)
    bool Muted,
    bool Muffled,
    string Reason,
    // Channel (Primitive 2) - two players with the same non-empty key hear each other.
    string ChannelKey,
    bool ChannelTwoWay,
    int ChannelShape,
    float ChannelVolume,
    // Optional spatial origin for a Proximity-shaped channel (e.g. a Medium seance spirit point).
    // When ChannelHasOrigin is true, the listener hears the speaker from ChannelOrigin with falloff.
    bool ChannelHasOrigin,
    Vector2 ChannelOrigin,
    // Listener-origin (Primitive 3) - local player only.
    bool ListenerActive,
    Vector2 ListenerOrigin,
    float ListenerLightRadius,
    bool ListenerReplace)
{
    public static readonly ExternalVoiceState None = new(
        false, false, "",
        "", true, 1, 1f,
        false, default,
        false, default, -1f, true);

    public bool HasReason => !string.IsNullOrEmpty(Reason);
}
