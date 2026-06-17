using System;
using System.Collections.Generic;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BclVoiceMixer
{
    private const int PrimeSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
    private const int FadeSamples = AudioHelpers.ClockRate / 100;
    private const int MaxWaitMs = AudioHelpers.PlaybackMaxPrebufferWaitMilliseconds;
    private const float GainGlideK = 0.002f;

    private sealed class Peer
    {
        public readonly float[] Ring = new float[AudioHelpers.ClockRate / 2];
        public int Read;
        public int Write;
        public int Count;
        public float Volume;
        public float ClientVolume = 1f;
        public float LeftGain = 1f;
        public float RightGain = 1f;
        public float CurLeft;
        public float CurRight;
        public bool Primed;
        public int FadeRemaining;
        public float LpState;
        public float OccCoef = 1f;
        public DateTime PrimeDeadline;
    }

    private readonly Dictionary<int, Peer> _peers = new();
    private readonly object _sync = new();
    private float _limiterGain = 1f;

    public void AddSamples(int group, float[] mono, int count, bool silent)
    {
        if (count <= 0) return;
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
            if (silent)
            {
                if (!p.Primed && p.Count > 0)
                    PrimeLocked(p);
                return;
            }
            if (!p.Primed && p.PrimeDeadline == DateTime.MinValue)
                p.PrimeDeadline = DateTime.UtcNow.AddMilliseconds(MaxWaitMs);
            var len = p.Ring.Length;
            for (var i = 0; i < count; i++)
            {
                if (p.Count >= len)
                {
                    p.Read = (p.Read + 1) % len;
                    p.Count--;
                }
                p.Ring[p.Write] = mono[i];
                p.Write = (p.Write + 1) % len;
                p.Count++;
            }
            if (!p.Primed && p.Count >= PrimeSamples)
                PrimeLocked(p);
        }
    }

    public void SetPeer(int group, float volume, float pan, float occlusion)
    {
        GetPanGains(pan, out var left, out var right);
        var coef = OcclusionToCoef(occlusion);
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
            p.Volume = volume;
            p.LeftGain = left;
            p.RightGain = right;
            p.OccCoef = coef;
        }
    }

    public void SetClientVolume(int group, float clientVolume)
    {
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
            p.ClientVolume = clientVolume;
        }
    }

    public void Remove(int group)
    {
        lock (_sync)
            _peers.Remove(group);
    }

    public void Read(float[] interleavedStereo)
    {
        Array.Clear(interleavedStereo, 0, interleavedStereo.Length);
        var frames = interleavedStereo.Length / 2;
        const float wInc = MathF.PI / FadeSamples;
        lock (_sync)
        {
            foreach (var p in _peers.Values)
            {
                if (!p.Primed)
                {
                    if (p.Count > 0 && p.PrimeDeadline != DateTime.MinValue && DateTime.UtcNow >= p.PrimeDeadline)
                        PrimeLocked(p);
                    else
                        continue;
                }
                var targetL = p.LeftGain * p.Volume * p.ClientVolume;
                var targetR = p.RightGain * p.Volume * p.ClientVolume;
                var n = Math.Min(frames, p.Count);
                var fadeOutStart = p.Count <= frames ? n - Math.Min(FadeSamples, n) : int.MaxValue;
                for (var f = 0; f < n; f++)
                {
                    var s = p.Ring[p.Read];
                    p.Read = (p.Read + 1) % p.Ring.Length;
                    p.Count--;
                    p.LpState += p.OccCoef * (s - p.LpState);
                    s = p.LpState;
                    if (p.FadeRemaining > 0)
                    {
                        s *= 0.5f * (1f - MathF.Cos((FadeSamples - p.FadeRemaining) * wInc));
                        p.FadeRemaining--;
                    }
                    if (f >= fadeOutStart)
                        s *= 0.5f * (1f - MathF.Cos((n - f) * wInc));
                    p.CurLeft += GainGlideK * (targetL - p.CurLeft);
                    p.CurRight += GainGlideK * (targetR - p.CurRight);
                    interleavedStereo[f * 2] += s * p.CurLeft;
                    interleavedStereo[f * 2 + 1] += s * p.CurRight;
                }
                if (p.Count == 0)
                {
                    p.Primed = false;
                    p.LpState = 0f;
                    p.PrimeDeadline = DateTime.MinValue;
                }
            }
        }

        var peak = AudioHelpers.MeasurePeak(interleavedStereo, interleavedStereo.Length);
        var target = AudioHelpers.GetPlaybackMixLimiterGain(peak);
        if (target < _limiterGain)
            _limiterGain = target;
        else
            _limiterGain = Math.Min(1f, _limiterGain + AudioHelpers.PlaybackMixLimiterReleasePerFrame);
        AudioHelpers.ApplyGain(interleavedStereo, interleavedStereo.Length, _limiterGain);
        for (var i = 0; i < interleavedStereo.Length; i++)
            interleavedStereo[i] = Math.Clamp(interleavedStereo[i], -1f, 1f);
    }

    private void PrimeLocked(Peer p)
    {
        p.Primed = true;
        p.PrimeDeadline = DateTime.MinValue;
        p.FadeRemaining = FadeSamples;
        p.LpState = 0f;
        p.CurLeft = p.LeftGain * p.Volume * p.ClientVolume;
        p.CurRight = p.RightGain * p.Volume * p.ClientVolume;
    }

    private static void GetPanGains(float pan, out float left, out float right)
    {
        pan = Math.Clamp(pan, -1f, 1f);
        const float farSide = 0.25f;
        var farGain = farSide + (1f - farSide) * MathF.Cos(Math.Abs(pan) * (MathF.PI / 2f));
        left = pan > 0f ? farGain : 1f;
        right = pan < 0f ? farGain : 1f;
    }

    private static float OcclusionToCoef(float occlusion)
    {
        occlusion = Math.Clamp(occlusion, 0f, 1f);
        return 0.02f + 0.98f * occlusion;
    }
}
