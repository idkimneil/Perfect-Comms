using System;
using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public abstract class OptionHolder
{
    public string Label { get; protected init; } = "";
    public Func<bool>? Visible { get; init; }
    public bool IsVisible => Visible == null || Visible();
}

public sealed class ToggleHolder : OptionHolder
{
    private readonly ConfigEntry<bool> _entry;

    public ToggleHolder(ConfigFile cfg, string section, string key, string label, bool def)
    {
        Label = label;
        _entry = cfg.Bind(section, key, def);
    }

    public bool Value
    {
        get => _entry.Value;
        set => _entry.Value = value;
    }
}

public sealed class EnumHolder : OptionHolder
{
    private readonly ConfigEntry<int> _entry;

    public Type EnumType { get; }
    public string[] Labels { get; }

    public EnumHolder(ConfigFile cfg, string section, string key, string label, int def, Type enumType, string[] labels)
    {
        Label = label;
        EnumType = enumType;
        Labels = labels;
        _entry = cfg.Bind(section, key, def);
    }

    public int Value
    {
        get => _entry.Value;
        set => _entry.Value = value;
    }
}

public sealed class NumberHolder : OptionHolder
{
    private readonly ConfigEntry<float> _entry;

    public float Min { get; }
    public float Max { get; }
    public float Step { get; }
    public string Format { get; }

    public NumberHolder(ConfigFile cfg, string section, string key, string label, float def, float min, float max, float step, string format)
    {
        Label = label;
        Min = min;
        Max = max;
        Step = step;
        Format = format;
        _entry = cfg.Bind(section, key, def, new ConfigDescription(label, new AcceptableValueRange<float>(min, max)));
    }

    public float Value
    {
        get => _entry.Value;
        set => _entry.Value = Math.Clamp(value, Min, Max);
    }
}
