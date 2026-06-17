using System;
using System.Runtime.InteropServices;
using ManagedBass;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BassRecorder : IDisposable
{
    private readonly RecordProcedure _proc;
    private readonly Action<float[], int> _onFrame;
    private readonly object _gate = new();
    private float[] _buffer = Array.Empty<float>();
    private int _stream;

    public BassRecorder(Action<float[], int> onFrame)
    {
        _onFrame = onFrame;
        _proc = RecordProc;
    }

    public bool Start(int device)
    {
        lock (_gate)
        {
            StopLocked();
            if (!Bass.RecordInit(device) && Bass.LastError != Errors.Already)
                return false;
            _stream = Bass.RecordStart(AudioHelpers.ClockRate, 1, BassFlags.Float, _proc);
            return _stream != 0;
        }
    }

    private bool RecordProc(int handle, IntPtr buffer, int length, IntPtr user)
    {
        var samples = length / 4;
        if (samples > 0)
        {
            if (_buffer.Length < samples)
                _buffer = new float[samples];
            Marshal.Copy(buffer, _buffer, 0, samples);
            try { _onFrame(_buffer, samples); } catch { }
        }
        return true;
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    private void StopLocked()
    {
        var h = _stream;
        _stream = 0;
        if (h != 0)
        {
            try { Bass.ChannelStop(h); } catch { }
            try { Bass.RecordFree(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
