using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceRoleIntegrationOptions
{
    public const string GroupName = "Perfect Comms: Role Voice Rules";
    public const uint GroupPriority = 1001;
    private const string Section = "Host.VoiceChat.Roles";

    public ToggleHolder MuteBlackmailedInMeetings { get; }
    public ToggleHolder MuteBlackmailedNextRound { get; }
    public ToggleHolder MuteParasiteControlled { get; }
    public ToggleHolder ParasiteHearFromVictim { get; }
    public ToggleHolder MutePuppeteerControlled { get; }
    public ToggleHolder PuppeteerHearFromVictim { get; }
    public ToggleHolder MuteSwooperWhileSwooped { get; }
    public ToggleHolder MuffleBlindedOrFlashedHearing { get; }
    public ToggleHolder MuffleHypnotizedDuringHysteria { get; }
    public ToggleHolder CrewpostorUsesImpostorVoice { get; }
    public ToggleHolder MuteGlitchHacked { get; }
    public ToggleHolder MuteJailedInMeetings { get; }
    public ToggleHolder JailPersistsAfterJailorDeath { get; }
    public ToggleHolder JailorCanUnmuteJailed { get; }
    public EnumHolder MediumGhostVoice { get; }

    private VoiceRoleIntegrationOptions(ConfigFile cfg)
    {
        MuteBlackmailedInMeetings = new ToggleHolder(cfg, Section, "MuteBlackmailedInMeetings", "<color=#FF0000><b>Blackmailer</b></color>: Mute Blackmailed in Meetings", true);
        MuteBlackmailedNextRound = new ToggleHolder(cfg, Section, "MuteBlackmailedNextRound", "<color=#FF0000><b>Blackmailer</b></color>: Mute Blackmailed Next Round", false);
        MuteParasiteControlled = new ToggleHolder(cfg, Section, "MuteParasiteControlled", "<color=#FF0000><b>Parasite</b></color>: Mute Controlled Victim", true);
        ParasiteHearFromVictim = new ToggleHolder(cfg, Section, "ParasiteHearFromVictim", "<color=#FF0000><b>Parasite</b></color>: Also Hear Controlled Victim", true);
        MutePuppeteerControlled = new ToggleHolder(cfg, Section, "MutePuppeteerControlled", "<color=#FF0000><b>Puppeteer</b></color>: Mute Controlled Victim", true);
        PuppeteerHearFromVictim = new ToggleHolder(cfg, Section, "PuppeteerHearFromVictim", "<color=#FF0000><b>Puppeteer</b></color>: Hear From Controlled Victim", true);
        MuteSwooperWhileSwooped = new ToggleHolder(cfg, Section, "MuteSwooperWhileSwooped", "<color=#FF0000><b>Swooper</b></color>: Mute While Swooped", true);
        MuffleBlindedOrFlashedHearing = new ToggleHolder(cfg, Section, "MuffleBlindedOrFlashedHearing", "<color=#FF0000><b>Eclipsal/Grenadier</b></color>: Muffle Blinded/Flashed Hearing", true);
        MuffleHypnotizedDuringHysteria = new ToggleHolder(cfg, Section, "MuffleHypnotizedDuringHysteria", "<color=#FF0000><b>Hypnotist</b></color>: Muffle Hypnotized During Hysteria", true);
        CrewpostorUsesImpostorVoice = new ToggleHolder(cfg, Section, "CrewpostorUsesImpostorVoice", "<color=#FF0000><b>Crewpostor</b></color>: Use Impostor Voice", true);
        MuteGlitchHacked = new ToggleHolder(cfg, Section, "MuteGlitchHacked", "<color=#00FF00><b>Glitch</b></color>: Mute Hacked Players", true);
        MuteJailedInMeetings = new ToggleHolder(cfg, Section, "MuteJailedInMeetings", "<color=#A6A6A6><b>Jailor</b></color>: Mute Jailee in Meetings", true);
        JailPersistsAfterJailorDeath = new ToggleHolder(cfg, Section, "JailPersistsAfterJailorDeath", "<color=#A6A6A6><b>Jailor</b></color>: Jail Persists If Jailor Dies", false)
        {
            Visible = JailSubOptionVisible
        };
        JailorCanUnmuteJailed = new ToggleHolder(cfg, Section, "JailorCanUnmuteJailed", "<color=#A6A6A6><b>Jailor</b></color>: Can Unmute Jailee", true);
        MediumGhostVoice = new EnumHolder(cfg, Section, "MediumGhostVoice", "<color=#A680FF><b>Medium</b></color>: Ghost Voice",
            (int)MediumGhostVoiceMode.None, typeof(MediumGhostVoiceMode),
            new[] { "None", "Medium -> Ghost", "Ghost -> Medium", "Both" });
    }

    private static VoiceRoleIntegrationOptions? _instance;
    public static VoiceRoleIntegrationOptions Instance => _instance ??= new VoiceRoleIntegrationOptions(VoiceChatPluginMain.PluginConfig);
    internal static VoiceRoleIntegrationOptions GetInstance() => Instance;

    private static bool JailSubOptionVisible() => Instance.MuteJailedInMeetings.Value;
}

public enum MediumGhostVoiceMode
{
    None,
    MediumToGhost,
    GhostToMedium,
    Both,
}
