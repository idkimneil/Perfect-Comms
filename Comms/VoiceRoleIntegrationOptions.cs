using MiraAPI.GameOptions;
using MiraAPI.GameOptions.OptionTypes;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceRoleIntegrationOptions : AbstractOptionGroup
{
    public override string GroupName => "Perfect Comms: Role Voice Rules";
    public override uint GroupPriority => 1001;

    public ModdedToggleOption MuteBlackmailedInMeetings { get; } = new("Mute Blackmailed in Meetings", true);
    public ModdedToggleOption MuteBlackmailedNextRound { get; } = new("Mute Blackmailed Next Round", false);
    public ModdedToggleOption MuteJailedInMeetings { get; } = new("Mute Jailed in Meetings", true);
    public ModdedToggleOption JailorCanUnmuteJailed { get; } = new("Jailor Can Unmute Jailed", true);
    public ModdedToggleOption MuteParasiteControlled { get; } = new("Mute Parasite-Controlled Player", true);
    public ModdedToggleOption MutePuppeteerControlled { get; } = new("Mute Puppeteer-Controlled Player", true);
    public ModdedToggleOption CrewpostorUsesImpostorVoice { get; } = new("Crewpostor Uses Impostor Voice", true);

    internal static VoiceRoleIntegrationOptions GetInstance() =>
        OptionGroupSingleton<VoiceRoleIntegrationOptions>.Instance;
}
