using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceSettingsPanel
{
    private const float PanelW = 1180f;
    private const float PanelH = 720f;
    private const float RowH = 72f;
    private const float TopPad = 12f;

    private static readonly string[] Categories =
        { "AUDIO", "DEVICES", "KEYBINDS", "HUD", "ADVANCED" };

    private static VoiceUiKit.PanelShell? _shell;
    private static VoiceUiKit.CategoryRail? _rail;
    private static readonly List<VoiceUiKit.Row> _rows = new();
    private static VoiceUiKit.Row? _activeRow;
    private static float _scroll;
    private static float _contentHeight;
    private static float _animT;

    private static bool ShellAlive => _shell != null && _shell.Root != null;
    private static bool _shown;
    public static bool IsOpen => ShellAlive && _shown;

    public static void Toggle()
    {
        if (_shown) Hide();
        else Show();
    }

    public static void Show()
    {
        if (VoiceSettings.Instance == null) return;

        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();

        bool rebuilt = false;
        if (!ShellAlive)
        {
            Destroy();
            Build();
            rebuilt = true;
        }

        _shell!.Root.SetActive(true);
        _shell.Group.alpha = 1f;
        _shell.Group.interactable = true;
        _shell.Group.blocksRaycasts = true;
        _shell.RootRect.localScale = Vector3.one;
        _scroll = 0f;
        _shell.PaneRoot.anchoredPosition = Vector2.zero;
        _animT = 1f;
        _shown = true;

        if (!rebuilt) RebuildRows();

        VoiceUiKit.RaiseAbove(_shell.RootRect);
    }

    private static void Build()
    {
        _shell = new VoiceUiKit.PanelShell("VC_SettingsPanel", "PERFECT COMMS", PanelW, PanelH, HeaderClose);
        _rail = new VoiceUiKit.CategoryRail();
        _rail.Build(_shell.RailRoot, _shell.RailWidth, Categories);
        _rail.OnSelect = _ => RebuildRows();
        RebuildRows();
    }

    public static void Hide()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        _shown = false;
        _animT = 0f;
        _activeRow = null;
        if (_shell != null && _shell.Root != null)
        {
            _shell.Group.alpha = 0f;
            _shell.Group.interactable = false;
            _shell.Group.blocksRaycasts = false;
            _shell.Root.SetActive(false);
        }
    }

    private static void HeaderClose()
    {
        VoiceUiKit.SwallowClick();
        Hide();
    }

    public static void ForceClose()
    {
        Hide();
    }

    private static void Destroy()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        if (_shell != null)
        {
            if (_shell.Root != null) Object.Destroy(_shell.Root);
            _shell = null;
        }
        _rail = null;
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _shown = false;
    }

    private static void RebuildRows()
    {
        if (_shell == null) return;
        VoiceUiKit.RebindRow.CancelCapture();
        for (int i = _shell.PaneRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.PaneRoot.GetChild(i).gameObject);
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _shell.PaneRoot.anchoredPosition = Vector2.zero;

        var defs = BuildCategory(_rail!.Selected);
        float y = -TopPad;
        for (int i = 0; i < defs.Count; i++)
        {
            var row = defs[i](_shell.PaneRoot, _shell.PaneWidth, y);
            if (row != null) _rows.Add(row);
            if (i < defs.Count - 1)
            {
                var div = VoiceUiKit.Panel("Div", _shell.PaneRoot, VoiceUiKit.Divider, false);
                div.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                div.rectTransform.sizeDelta = new Vector2(-20f, 1f);
                div.rectTransform.anchoredPosition = new Vector2(0f, y - RowH + 1f);
            }
            y -= RowH;
        }
        _contentHeight = TopPad + defs.Count * RowH;

        if (defs.Count == 0)
        {
            var empty = VoiceUiKit.Text("Empty", _shell.PaneRoot, "No options", 16f,
                VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Center);
            empty.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            empty.rectTransform.sizeDelta = new Vector2(0f, 40f);
            empty.rectTransform.anchoredPosition = new Vector2(0f, -30f);
        }
    }

    private delegate VoiceUiKit.Row? RowDef(RectTransform pane, float paneW, float y);

    private static List<RowDef> BuildCategory(int cat)
    {
        var defs = new List<RowDef>();
        var s = VoiceSettings.Instance!;
        switch (cat)
        {
            case 0: BuildAudio(defs, s); break;
            case 1: BuildDevices(defs, s); break;
            case 2: BuildKeybinds(defs, s); break;
            case 3: BuildHud(defs, s); break;
            case 4: BuildAdvanced(defs, s); break;
        }
        return defs;
    }

    private static Func<float, string> Pct => v => $"<color=#22D3EE>{Mathf.RoundToInt(v * 100f)}%</color>";
    private static Func<float, string> Num2 => v => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static void Slider(List<RowDef> defs, string label,
        BepInEx.Configuration.ConfigEntry<float> entry, Func<float, string> fmt)
    {
        var range = GetRange(entry);
        defs.Add((pane, paneW, y) => new VoiceUiKit.SliderRow(
            () => entry.Value, v => entry.Value = v, range.x, range.y, fmt)
            .Build(pane, label, paneW, y, RowH));
    }

    private static void Toggle(List<RowDef> defs, string label,
        BepInEx.Configuration.ConfigEntry<bool> entry)
    {
        defs.Add((pane, paneW, y) => new VoiceUiKit.ToggleRow(() => entry.Value, v => entry.Value = v)
            .Build(pane, label, paneW, y, RowH));
    }

    private static void EnumStep<TEnum>(List<RowDef> defs, string label,
        BepInEx.Configuration.ConfigEntry<TEnum> entry, string[] labels) where TEnum : struct, Enum
    {
        defs.Add((pane, paneW, y) => new VoiceUiKit.StepperRow(
            () => Convert.ToInt32(entry.Value),
            i => entry.Value = (TEnum)Enum.ToObject(typeof(TEnum), i),
            () => labels.Length,
            i => labels[Mathf.Clamp(i, 0, labels.Length - 1)])
            .Build(pane, label, paneW, y, RowH));
    }

    private static void BuildAudio(List<RowDef> defs, VoiceChatLocalSettings s)
    {
        Slider(defs, "Mic Volume", s.MicVolume, Pct);
        Slider(defs, "Mic Sensitivity", s.MicSensitivity, Num2);
        Slider(defs, "Speaker Volume", s.MasterVolume, Pct);
        EnumStep(defs, "Mic Mode", s.MicMode, new[] { "Open Mic", "Push To Talk" });
        Toggle(defs, "Noise Suppression", s.NoiseSuppressionEnabled);
        Toggle(defs, "Auto Mic Gain", s.AutoMicGain);
        Toggle(defs, "Nat Fix", s.NatFix);
        Slider(defs, "Voice Falloff Softness", s.VoiceFalloffSoftness, Pct);
        Toggle(defs, "Start Muted", s.StartMuted);
        Toggle(defs, "Start Deafened", s.StartDeafened);
    }

    private static void BuildDevices(List<RowDef> defs, VoiceChatLocalSettings s)
    {
        VoiceChatLocalSettings.MaybeRefreshDeviceLists();

        defs.Add((pane, paneW, y) => new VoiceUiKit.StepperRow(
            () => (int)s.MicrophoneDeviceIndex.Value,
            i => s.MicrophoneDeviceIndex.Value = (MicDeviceEnum)i,
            () => VoiceChatLocalSettings.MicDeviceNames.Length,
            i => DeviceName(VoiceChatLocalSettings.MicDeviceNames, i))
            .Build(pane, "Microphone", paneW, y, RowH));

#if WINDOWS
        defs.Add((pane, paneW, y) => new VoiceUiKit.StepperRow(
            () => (int)s.SpeakerDeviceIndex.Value,
            i => s.SpeakerDeviceIndex.Value = (SpkDeviceEnum)i,
            () => VoiceChatLocalSettings.SpkDeviceNames.Length,
            i => DeviceName(VoiceChatLocalSettings.SpkDeviceNames, i))
            .Build(pane, "Speaker", paneW, y, RowH));
#endif
    }

    private static string DeviceName(string[] names, int i)
    {
        if (names.Length == 0) return "<color=#607282>No devices found</color>";
        return names[Mathf.Clamp(i, 0, names.Length - 1)];
    }

    private static void Rebind(List<RowDef> defs, VoiceKeybind bind)
    {
        defs.Add((pane, paneW, y) => new VoiceUiKit.RebindRow(
            () => bind.CurrentKey, k => bind.Set(k), () => bind.Clear())
            .Build(pane, bind.DisplayName, paneW, y, RowH));
    }

    private static void BuildKeybinds(List<RowDef> defs, VoiceChatLocalSettings s)
    {
        Rebind(defs, VoiceChatKeybinds.ToggleMute);
        Rebind(defs, VoiceChatKeybinds.PushToTalk);
        Rebind(defs, VoiceChatKeybinds.TeamRadio);
        Rebind(defs, VoiceChatKeybinds.CycleTeamRadioChannel);
        Rebind(defs, VoiceChatKeybinds.ToggleMicMode);
        Rebind(defs, VoiceChatKeybinds.ToggleSpeaker);
        Rebind(defs, VoiceChatKeybinds.VolumeMenu);
        Rebind(defs, VoiceChatKeybinds.LocalVoiceRefresh);
        Rebind(defs, VoiceChatKeybinds.HostVoiceRefresh);
    }

    private static void BuildHud(List<RowDef> defs, VoiceChatLocalSettings s)
    {
        Slider(defs, "Button Position X", s.ButtonPositionX, Pct);
        Slider(defs, "Button Position Y", s.ButtonPositionY, Pct);
        EnumStep(defs, "Controls Layout", s.VoiceControlsLayout, new[] { "Vertical", "Horizontal" });
        EnumStep(defs, "Speaking Bar Position", s.SpeakingBarPosition, new[]
        {
            "Top Left", "Top Middle", "Top Right", "Bottom Left", "Bottom Middle", "Bottom Right",
            "Middle Left", "Middle Right"
        });
        EnumStep(defs, "Speaking Bar Layout", s.SpeakingBarLayout, new[] { "Vertical", "Horizontal" });
        EnumStep(defs, "Speaking Bar Name Pos", s.SpeakingBarNamePosition, new[] { "Bottom", "Top", "Left", "Right" });
        Toggle(defs, "Speaking Bar Manual Layout", s.SpeakingBarManualLayout);
        Toggle(defs, "Speaking Bar Backdrop", s.SpeakingBarBackdrop);
        Toggle(defs, "Meeting Speaking Overlay", s.MeetingSpeakingOverlay);
        EnumStep(defs, "Jail Unmute Placement", s.JailUnmuteButtonPlacement, new[] { "Voice HUD", "Meeting Card" });
        Slider(defs, "Speaking Bar X", s.SpeakingBarX, Pct);
        Slider(defs, "Speaking Bar Y", s.SpeakingBarY, Pct);
        Slider(defs, "Overlay Scale", s.OverlayScale, Num2);
    }

    private static void BuildAdvanced(List<RowDef> defs, VoiceChatLocalSettings s)
    {
        Toggle(defs, "Debug Voice Stats", s.DebugVoiceStats);
        Toggle(defs, "Mic Calibration Diagnostics", s.MicCalibrationDiagnostics);
    }

    private static Vector2 GetRange(BepInEx.Configuration.ConfigEntryBase entry)
    {
        var desc = entry.Description;
        if (desc?.AcceptableValues is BepInEx.Configuration.AcceptableValueRange<float> r)
            return new Vector2((float)r.MinValue, (float)r.MaxValue);
        return new Vector2(0f, 1f);
    }

    public static void Tick()
    {
        if (_shell == null) return;
        if (_shell.Root == null) { Destroy(); return; }
        if (!_shown) return;

        float dt = Time.deltaTime;
        if (_animT < 1f)
        {
            _animT = Mathf.Min(1f, _animT + dt / 0.12f);
            ApplyOpenAnim();
        }

        _shell.TickHeader();
        if (_shell == null) return;
        _rail!.Tick();
        HandleScroll();
        HandleInput();
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);
    }

    private static void ApplyOpenAnim()
    {
        if (_shell == null) return;
        float e = 1f - (1f - _animT) * (1f - _animT);
        float scale = Mathf.Lerp(0.97f, 1f, e);
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.Group.alpha = e;
    }

    private static void HandleScroll()
    {
        if (_shell == null) return;
        float viewH = _shell.PaneHeight - 24f;
        float maxScroll = Mathf.Max(0f, _contentHeight - viewH);
        if (maxScroll <= 0f) return;
        if (!VoiceUiKit.Contains(_shell.PaneClip)) return;
        float dy = Input.mouseScrollDelta.y;
        if (dy > -0.01f && dy < 0.01f) return;
        _scroll = Mathf.Clamp(_scroll - dy * RowH, 0f, maxScroll);
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
    }

    private static void HandleInput()
    {
        if (!Input.GetMouseButton(0))
        {
            if (_activeRow != null) { _activeRow.OnMouseUp(); _activeRow = null; }
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].OnMouseDown();
            _activeRow = FindDragging();
        }
        else if (_activeRow != null)
        {
            _activeRow.OnMouseDrag();
        }
    }

    private static VoiceUiKit.Row? FindDragging()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].IsDragging) return _rows[i];
        return null;
    }
}
