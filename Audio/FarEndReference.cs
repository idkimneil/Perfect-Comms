using System;

namespace VoiceChatPlugin.Audio;

// Shared far-end reference for acoustic echo cancellation: the playback (render) thread pushes the mono
// downmix of what the speaker actually plays; the capture thread drains it one mic-frame at a time to feed
// the echo canceller. CircularFloatBuffer makes the single-producer/single-consumer handoff lock-safe.
internal sealed class FarEndReference
{
    private readonly CircularFloatBuffer _buffer;
    private readonly int _maxFill;
    private float[] _mono = Array.Empty<float>();

    public volatile bool Enabled;

    public FarEndReference(int capacitySamples, int maxFillSamples)
    {
        _buffer = new CircularFloatBuffer(capacitySamples);
        _maxFill = maxFillSamples;
    }

    // Render thread: store the mono downmix of the just-rendered stereo frame. Samples past leftRead/rightRead
    // are genuine played silence. Caps occupancy so a render burst or slow drift can't grow the reference
    // delay without bound (which would desync the adaptive filter).
    public void WriteStereoDownmix(float[] left, int leftRead, float[] right, int rightRead, int frames)
    {
        if (!Enabled || frames <= 0) return;
        if (_mono.Length < frames) _mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float l = i < leftRead ? left[i] : 0f;
            float r = i < rightRead ? right[i] : 0f;
            _mono[i] = (l + r) * 0.5f;
        }
        _buffer.Write(_mono, 0, frames);
        int over = _buffer.Count - _maxFill;
        if (over > 0) _buffer.Discard(over);
    }

    // Capture thread: drain exactly count samples aligned with the current mic frame. Returns false when fewer
    // than count are buffered (startup / render starvation), in which case the caller skips cancellation.
    public bool TryReadAligned(float[] destination, int count)
    {
        if (count <= 0 || destination.Length < count) return false;
        if (_buffer.Count < count) return false;
        return _buffer.Read(destination, 0, count) == count;
    }

    public void Reset() => _buffer.Reset();
}
