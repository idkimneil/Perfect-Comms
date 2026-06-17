#if ANDROID
using System;
using System.Collections.Generic;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class AndroidVoiceMixer
{
    private sealed class Peer
    {
        public readonly float[] Ring = new float[AudioHelpers.ClockRate / 2];
        public int Read;
        public int Write;
        public int Count;
        public float Volume = 1f;
        public float ClientVolume = 1f;
        public float LeftGain = 1f;
        public float RightGain = 1f;
    }

    private readonly Dictionary<int, Peer> _peers = new();
    private readonly object _sync = new();

    public void AddSamples(int group, float[] mono, int count)
    {
        if (count <= 0) return;
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
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
        }
    }

    public void SetPeer(int group, float volume, float pan)
    {
        GetPanGains(pan, out var left, out var right);
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
            p.Volume = volume;
            p.LeftGain = left;
            p.RightGain = right;
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
        lock (_sync)
        {
            foreach (var p in _peers.Values)
            {
                var lg = p.LeftGain * p.Volume * p.ClientVolume;
                var rg = p.RightGain * p.Volume * p.ClientVolume;
                var n = Math.Min(frames, p.Count);
                for (var f = 0; f < n; f++)
                {
                    var s = p.Ring[p.Read];
                    p.Read = (p.Read + 1) % p.Ring.Length;
                    p.Count--;
                    interleavedStereo[f * 2] += s * lg;
                    interleavedStereo[f * 2 + 1] += s * rg;
                }
            }
        }
        for (var i = 0; i < interleavedStereo.Length; i++)
            interleavedStereo[i] = Math.Clamp(interleavedStereo[i], -1f, 1f);
    }

    private static void GetPanGains(float pan, out float left, out float right)
    {
        pan = Math.Clamp(pan, -1f, 1f);
        const float farSide = 0.25f;
        var farGain = farSide + (1f - farSide) * MathF.Cos(Math.Abs(pan) * (MathF.PI / 2f));
        left = pan > 0f ? farGain : 1f;
        right = pan < 0f ? farGain : 1f;
    }
}
#endif
