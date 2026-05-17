using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MiraAPI.LocalSettings;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatHudState
{
    // ── Grid-injected buttons ─────────────────────────────────────────────────
    // These are parented directly into HudManagerPatches.UiTopRight so they
    // appear at the end of the main TOU-Mira button row and are managed by its
    // GridArrange layout. No AspectPosition / floating-window logic needed.

    private static GameObject? _micButtonObj;
    private static PassiveButton? _micButton;
    private static GameObject? _spkButtonObj;
    private static PassiveButton? _spkButton;
    private static GameObject? _jailButtonObj;
    private static PassiveButton? _jailButton;

    // ── Tooltip objects (parented to HUD root, not the grid) ─────────────────
    private static GameObject?  _micTooltip;
    private static GameObject?  _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;

    // ── State ─────────────────────────────────────────────────────────────────
    private static bool _micMuted;
    private static bool _impostorHeld;
    private static bool _pushToTalkHeld;
    private static bool _speakerMuted;

    public static bool IsMuted        => _micMuted;
    public static bool IsImpostorRadio => _impostorHeld && CanUseImpostorRadio();
    public static bool IsSpeakerMuted  => _speakerMuted;

    // ── Init ──────────────────────────────────────────────────────────────────

    internal static void Init()
    {
        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((_, __) =>
            {
                DestroyButtons();
                DestroyTooltips();
            });

        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings != null)
        {
            _micMuted     = settings.StartMuted.Value;
            _speakerMuted = settings.StartDeafened.Value;
        }
    }

    // ── Public HUD entry point (called every frame by VCManager) ─────────────

    internal static void UpdateHud()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;

        EnsureGridButtons(hud);
        EnsureTooltips(hud);
        VoiceRoleMuteState.Update();
        ApplyMicState();
        UpdateJailButtonVisibility();
        RefreshButtonVisuals();
    }

    // ── Grid button creation ──────────────────────────────────────────────────
    // We wait for HudManagerPatches.UiTopRight to be set up (it happens in
    // CreateUiRow which runs from HudManager.Update). Once available we parent
    // our buttons into it; GridArrange.ArrangeChilds() will position them.

    private static void EnsureGridButtons(HudManager hud)
    {
        // UiTopRight is set by the TOU-Mira HudManagerPatches.CreateUiRow().
        // Access it via reflection so we don't need a hard compile dependency.
        var uiTopRight = GetUiTopRight();
        if (uiTopRight == null) return;

        if (_micButtonObj == null)
        {
            _micButtonObj = CreateGridButton(hud, uiTopRight, "VC_MicButton",
                "VoiceChatPlugin.Resources.MicOn.png", ToggleMutePublic,
                ShowMicTooltip, HideTooltips);
            _micButton = _micButtonObj.GetComponent<PassiveButton>();
        }

        if (_spkButtonObj == null)
        {
            _spkButtonObj = CreateGridButton(hud, uiTopRight, "VC_SpkButton",
                "VoiceChatPlugin.Resources.SpeakerOn.png", ToggleSpeakerPublic,
                ShowSpeakerTooltip, HideTooltips);
            _spkButton = _spkButtonObj.GetComponent<PassiveButton>();
        }

        if (_jailButtonObj == null)
        {
            _jailButtonObj = CreateGridButton(hud, uiTopRight, "VC_JailUnmuteButton",
                "VoiceChatPlugin.Resources.JailUnmute.png", JailUnmutePublic,
                null, null);
            _jailButton = _jailButtonObj.GetComponent<PassiveButton>();
            _jailButtonObj.SetActive(false);
        }

        // Keep buttons at the end of the sibling list so they appear last in the row.
        _micButtonObj.transform.SetAsLastSibling();
        _spkButtonObj.transform.SetAsLastSibling();
        _jailButtonObj.transform.SetAsLastSibling();
    }

    private static GameObject CreateGridButton(
        HudManager hud,
        GameObject parent,
        string name,
        string iconResource,
        Action onClick,
        Action? onMouseOver,
        Action? onMouseOut)
    {
        // Clone the MapButton — same approach TOU-Mira uses for its extra buttons.
        var go = Object.Instantiate(hud.MapButton.gameObject, parent.transform);
        go.name = name;

        // Remove any AspectPosition so the GridArrange owns the layout entirely.
        var ap = go.GetComponent<AspectPosition>();
        if (ap != null) Object.Destroy(ap);

        // Clear the default sprites so only our icon is visible.
        ClearButtonBG(go);

        // Replace the sprite on the Inactive child with our icon.
        CreateIconChild(go, iconResource);

        // Wire up click / hover.
        var pb = go.GetComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((UnityAction)(() => onClick()));
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut  = new UnityEvent();
        if (onMouseOver != null)
            pb.OnMouseOver.AddListener((UnityAction)(() => onMouseOver()));
        if (onMouseOut != null)
            pb.OnMouseOut.AddListener((UnityAction)(() => onMouseOut()));

        return go;
    }

    // ── Jail button visibility ────────────────────────────────────────────────

    private static void UpdateJailButtonVisibility()
    {
        if (_jailButtonObj == null) return;
        _jailButtonObj.SetActive(VoiceRoleMuteState.CanLocalJailorUnmute(out _));
    }

    // ── Tooltip helpers (unchanged logic, parented to HUD root) ──────────────

    private static void EnsureTooltips(HudManager hud)
    {
        var root = hud.transform.parent != null ? hud.transform.parent : hud.transform;
        if (_micTooltip == null)
            _micTooltip = CreateTooltipObject(root, out _micTooltipTmp);
        if (_spkTooltip == null)
            _spkTooltip = CreateTooltipObject(root, out _spkTooltipTmp);
    }

    private static void ShowMicTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _micButtonObj == null) return;

        string status = _micMuted ? "Muted"
            : IsInImpostorRadioMode() ? "Impostor Radio (held)"
            : "Active";

        var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        string muteKey  = VoiceChatKeybinds.ToggleMute.CurrentKey.ToString();
        string radioKey = VoiceChatKeybinds.ImpostorRadio.CurrentKey.ToString();

        _micTooltipTmp.text =
            "<b>Microphone</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)(tab.MicVolume.Value * 100f)}%\n" +
            $"Mute: {muteKey}  |  Imp. Radio: {radioKey} (hold)";

        PositionNear(_micTooltip, _micButtonObj);
        _micTooltip.SetActive(true);
    }

    private static void ShowSpeakerTooltip()
    {
        if (_spkTooltip == null || _spkTooltipTmp == null || _spkButtonObj == null) return;

        string status = _speakerMuted ? "Muted" : "Active";
        var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        string hotkey = VoiceChatKeybinds.ToggleSpeaker.CurrentKey.ToString();

        _spkTooltipTmp.text =
            "<b>Speaker</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)(tab.MasterVolume.Value * 100f)}%\n" +
            $"Hotkey: {hotkey}";

        PositionNear(_spkTooltip, _spkButtonObj);
        _spkTooltip.SetActive(true);
    }

    private static void HideTooltips()
    {
        _micTooltip?.SetActive(false);
        _spkTooltip?.SetActive(false);
    }

    private static void PositionNear(GameObject tooltip, GameObject btn)
    {
        var p = btn.transform.position;
        tooltip.transform.position = new Vector3(p.x - 0.2f, p.y - 0.9f, p.z - 1f);
    }

    private static GameObject CreateTooltipObject(Transform root, out TextMeshPro tmp)
    {
        var go = new GameObject("VC_Tooltip");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, 0f, -80f);

        var bg = new GameObject("BG");
        bg.transform.SetParent(go.transform, false);
        var bgSr = bg.AddComponent<SpriteRenderer>();
        bgSr.sprite = CreateSolidSprite(new Color(0f, 0f, 0f, 0.82f));
        bgSr.sortingLayerName = VCSorting.Layer;
        bgSr.sortingOrder = 32761;
        bg.transform.localScale = new Vector3(2.6f, 2.0f, 1f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -80f);
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 1.5f;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder = 32762;
        tmp.rectTransform.sizeDelta = new Vector2(2.4f, 1.8f);
        go.SetActive(false);
        return go;
    }

    // ── Button visual refresh ─────────────────────────────────────────────────

    private static void RefreshButtonVisuals()
    {
        if (_micButtonObj != null)
        {
            var sr = _micButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                if (VoiceRoleMuteState.TryGetLocalMeetingVoiceBlockReason(out _))
                {
                    sr.sprite = Sprites.MicOff;
                    sr.color  = new Color(1f, 0.65f, 0.15f);
                }
                else if (_micMuted)
                {
                    sr.sprite = Sprites.MicOff;
                    sr.color  = new Color(1f, 0.4f, 0.4f);
                }
                else if (IsInImpostorRadioMode())
                {
                    sr.sprite = Sprites.MicOn;
                    sr.color  = new Color(1f, 0.55f, 0.1f);
                }
                else
                {
                    sr.sprite = Sprites.MicOn;
                    sr.color  = Color.white;
                }
            }
        }

        if (_spkButtonObj != null)
        {
            var sr = _spkButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.sprite = _speakerMuted ? Sprites.SpkOff : Sprites.SpkOn;
                sr.color  = _speakerMuted ? new Color(1f, 0.4f, 0.4f) : Color.white;
            }
        }

        if (_jailButtonObj != null)
        {
            var sr = _jailButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.sprite = Sprites.JailUnmute;
                sr.color  = Color.white;
            }
        }
    }

    // ── State mutators ────────────────────────────────────────────────────────

    internal static void ApplyMicState()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        bool radioTransmit  = IsInImpostorRadioMode();
        bool pushToTalkMuted = settings?.MicMode.Value == VoiceMicMode.PushToTalk
                               && !_pushToTalkHeld
                               && !radioTransmit;
        bool roleMuted = VoiceRoleMuteState.IsLocalMeetingVoiceBlocked();
        VoiceChatRoom.Current?.SetMute(_micMuted || pushToTalkMuted || roleMuted);
    }

    internal static void ApplySpeakerState()
    {
        if (_speakerMuted)
        {
            VoiceChatRoom.Current?.SetMasterVolume(0f);
        }
        else
        {
            var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
            if (tab != null)
                VoiceChatRoom.Current?.SetMasterVolume(tab.MasterVolume.Value);
        }
    }

    internal static void TrySyncHostRoomSettings() { }

    internal static void ToggleMutePublic() => SetMuted(!_micMuted);

    internal static void SetMuted(bool muted)
    {
        _micMuted = muted;
        ApplyMicState();
        if (muted)
            MeetingSpeakingIndicatorPatch.ClearLocalIndicator();
        RefreshButtonVisuals();
    }

    internal static void ToggleSpeakerPublic() => SetSpeakerMuted(!_speakerMuted);

    internal static void SetSpeakerMuted(bool muted)
    {
        _speakerMuted = muted;
        ApplySpeakerState();
        RefreshButtonVisuals();
    }

    internal static void JailUnmutePublic()
    {
        VoiceRoleMuteState.LocalJailorAllowVoice();
        UpdateJailButtonVisibility();
        RefreshButtonVisuals();
    }

    internal static void UpdateImpostorRadioHold(bool held, bool justPressed, bool justReleased)
    {
        if (!CanUseImpostorRadio())
        {
            if (_impostorHeld)
            {
                _impostorHeld = false;
                ApplyMicState();
                RefreshButtonVisuals();
            }
            return;
        }

        bool prev = _impostorHeld;
        _impostorHeld = held;
        if (prev != _impostorHeld)
        {
            ApplyMicState();
            RefreshButtonVisuals();
        }
    }

    internal static bool IsInImpostorRadioMode()
        => _impostorHeld && CanUseImpostorRadio() && !_micMuted;

    internal static void UpdatePushToTalkHeld(bool held)
    {
        if (_pushToTalkHeld == held) return;
        _pushToTalkHeld = held;
        ApplyMicState();
        RefreshButtonVisuals();
    }

    // ── Removed stubs (were no-ops or position-specific) ─────────────────────
    // ApplyIndicatorPosition, ApplyOverlayScale — no longer needed; layout is
    // handled entirely by the TOU-Mira GridArrange.

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private static void DestroyButtons()
    {
        if (_micButtonObj  != null) { Object.Destroy(_micButtonObj);  _micButtonObj  = null; }
        if (_spkButtonObj  != null) { Object.Destroy(_spkButtonObj);  _spkButtonObj  = null; }
        if (_jailButtonObj != null) { Object.Destroy(_jailButtonObj); _jailButtonObj = null; }
        _micButton = null; _spkButton = null; _jailButton = null;
    }

    private static void DestroyTooltips()
    {
        if (_micTooltip != null) { Object.Destroy(_micTooltip); _micTooltip = null; }
        if (_spkTooltip != null) { Object.Destroy(_spkTooltip); _spkTooltip = null; }
        _micTooltipTmp = null; _spkTooltipTmp = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads HudManagerPatches.UiTopRight via reflection so we don't need
    /// a hard compile-time dependency on the TOU-Mira assembly from this file.
    /// Returns null if the field hasn't been set yet.
    /// </summary>
    private static GameObject? GetUiTopRight()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("TownOfUs.Patches.HudManagerPatches");
                if (t == null) continue;
                var fi = t.GetField("UiTopRight",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return fi?.GetValue(null) as GameObject;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool CanUseImpostorRadio()
        => PlayerControl.LocalPlayer != null
        && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true
        && PlayerControl.LocalPlayer.Data?.IsDead == false
        && VoiceChatGameOptions.Instance.ImpostorPrivateRadio.Value;

    private static void ClearButtonBG(GameObject obj)
    {
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>())
            sr.color = Color.clear;
    }

    private static void CreateIconChild(GameObject parent, string resource)
    {
        var go = new GameObject("VCIcon");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = parent.layer;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = LoadSprite(resource);
        sr.sortingLayerName = VCSorting.Layer;
        sr.sortingOrder = 32760;
    }

    private static Sprite CreateSolidSprite(Color c)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, c);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }

    private static readonly Dictionary<string, Sprite> _spriteCache = new();

    public static Sprite LoadSprite(string path)
    {
        if (_spriteCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp };
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 900f);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[path] = spr;
            return spr;
        }
        catch
        {
            VoiceChatPluginMain.Logger.LogError("[VC] Sprite load failed: " + path);
            return null!;
        }
    }

    private static class Sprites
    {
        public static Sprite MicOn      => LoadSprite("VoiceChatPlugin.Resources.MicOn.png");
        public static Sprite MicOff     => LoadSprite("VoiceChatPlugin.Resources.MicOff.png");
        public static Sprite SpkOn      => LoadSprite("VoiceChatPlugin.Resources.SpeakerOn.png");
        public static Sprite SpkOff     => LoadSprite("VoiceChatPlugin.Resources.SpeakerOff.png");
        public static Sprite JailUnmute => LoadSprite("VoiceChatPlugin.Resources.JailUnmute.png");
    }
}