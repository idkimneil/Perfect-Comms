#if MACOS
using System;
using System.Runtime.InteropServices;
using System.Threading;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin.Audio;

internal sealed class MacOsFullDuplexAudioEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly string _owner;
    private readonly MacOsAudioBridge.CaptureCallback _captureCallback;
    private readonly MacOsAudioBridge.PlaybackCallback _playbackCallback;
    private Action<float[], int>? _captureSink;
    private Func<float[], int, int>? _playbackSource;
    private GCHandle _selfHandle;
    private float[] _captureScratch = Array.Empty<float>();
    private float[] _captureMono = Array.Empty<float>();
    private float[] _playbackScratch = Array.Empty<float>();
    private long _captureCallbacks;
    private long _playbackCallbacks;
    private long _captureFrames;
    private long _playbackFrames;
    private long _underruns;
    private long _overruns;
    private long _callbackErrors;
    private long _restarts;
    private bool _running;

    // Fix #10: Track whether the native device has been fully stopped before
    // freeing the GCHandle. On CrossOver, CoreAudio callback teardown is async
    // and freeing the handle while a callback is in-flight corrupts the GC heap.
    // We use a semaphore to drain in-flight callbacks before freeing.
    private int _inFlightCallbacks;
    private readonly ManualResetEventSlim _callbacksDrained = new(true);
    private volatile bool _disposed;

    public MacOsFullDuplexAudioEngine(string owner)
    {
        _owner = owner;
        _captureCallback = OnNativeCapture;
        _playbackCallback = OnNativePlayback;
        _selfHandle = GCHandle.Alloc(this);
    }

    public MacOsAudioStartupResult Start(
        MacOsVoiceAudioConfig config,
        Action<float[], int> captureSink,
        Func<float[], int, int> playbackSource)
    {
        lock (_sync)
        {
            StopLocked("restart");
            _captureSink = captureSink;
            _playbackSource = playbackSource;
            Interlocked.Increment(ref _restarts);

            MacOsAudioDiagnostics.LogSelection(config.Backend, config.InputSelection, config.OutputSelection);
            var result = MacOsAudioBridge.StartFullDuplex(
                config.Backend,
                config.InputSelection,
                config.OutputSelection,
                config.SampleRate,
                config.InputChannels,
                config.OutputChannels,
                _captureCallback,
                _playbackCallback,
                GCHandle.ToIntPtr(_selfHandle));

            _running = result.Success;
            MacOsAudioDiagnostics.LogStartup(result, config.Backend);
            if (!result.Success)
            {
                _captureSink = null;
                _playbackSource = null;
                MacOsAudioBridge.StopFullDuplex();
            }

            return result;
        }
    }

    public void Stop(string reason)
    {
        lock (_sync)
            StopLocked(reason);
    }

    public MacOsAudioDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => new(
            Interlocked.Read(ref _captureCallbacks),
            Interlocked.Read(ref _playbackCallbacks),
            Interlocked.Read(ref _captureFrames),
            Interlocked.Read(ref _playbackFrames),
            Interlocked.Read(ref _underruns),
            Interlocked.Read(ref _overruns),
            Interlocked.Read(ref _callbackErrors),
            Interlocked.Read(ref _restarts),
            MacOsAudioBridge.GetStatsJson());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop("dispose");

        // Fix #10: Wait for any in-flight callbacks to finish before freeing
        // the GCHandle. CrossOver's CoreAudio emulation may invoke callbacks
        // after StopFullDuplex returns, unlike real macOS. Cap the wait at
        // 500ms to avoid hanging if CrossOver drops the callback entirely.
        if (!_callbacksDrained.Wait(TimeSpan.FromMilliseconds(500)))
        {
            VoiceDiagnostics.Log("mac.audio.dispose",
                $"backend={_owner} warning=callbacks-not-drained-in-time inFlight={_inFlightCallbacks}");
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        _callbacksDrained.Dispose();
    }

    private void StopLocked(string reason)
    {
        if (_running)
            VoiceDiagnostics.Log("mac.audio.stop", $"backend={_owner} reason={reason} nativeStop=true");
        _running = false;
        _captureSink = null;
        _playbackSource = null;
        MacOsAudioBridge.StopFullDuplex();
    }

    // Fix #10: Track entry/exit of callbacks so Dispose can drain them.
    private void EnterCallback()
    {
        // Reset the event when the first callback enters.
        if (Interlocked.Increment(ref _inFlightCallbacks) == 1)
            _callbacksDrained.Reset();
    }

    private void ExitCallback()
    {
        if (Interlocked.Decrement(ref _inFlightCallbacks) == 0)
            _callbacksDrained.Set();
    }

    private static MacOsFullDuplexAudioEngine? FromUserData(IntPtr userData)
    {
        if (userData == IntPtr.Zero) return null;
        try { return GCHandle.FromIntPtr(userData).Target as MacOsFullDuplexAudioEngine; }
        catch { return null; }
    }

    private static void OnNativeCapture(IntPtr samples, int frameCount, int channels, IntPtr userData)
        => FromUserData(userData)?.HandleNativeCapture(samples, frameCount, channels);

    private static int OnNativePlayback(IntPtr samples, int frameCount, int channels, IntPtr userData)
        => FromUserData(userData)?.HandleNativePlayback(samples, frameCount, channels) ?? 0;

    private void HandleNativeCapture(IntPtr samples, int frameCount, int channels)
    {
        // Fix #10: Guard against callbacks arriving after disposal.
        if (_disposed || samples == IntPtr.Zero || frameCount <= 0 || channels <= 0) return;
        EnterCallback();
        try
        {
            Interlocked.Increment(ref _captureCallbacks);
            Interlocked.Add(ref _captureFrames, frameCount);
            try
            {
                var sampleCount = checked(frameCount * channels);
                EnsureScratch(ref _captureScratch, sampleCount);
                Marshal.Copy(samples, _captureScratch, 0, sampleCount);

                EnsureScratch(ref _captureMono, frameCount);
                if (channels == 1)
                {
                    Array.Copy(_captureScratch, _captureMono, frameCount);
                }
                else
                {
                    for (var frame = 0; frame < frameCount; frame++)
                    {
                        var sum = 0f;
                        var baseIndex = frame * channels;
                        for (var channel = 0; channel < channels; channel++)
                            sum += _captureScratch[baseIndex + channel];
                        _captureMono[frame] = Math.Clamp(sum / channels, -1f, 1f);
                    }
                }

                _captureSink?.Invoke(_captureMono, frameCount);
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _callbackErrors) == 1)
                    VoiceDiagnostics.Log("mac.audio.callback_error",
                        $"backend={_owner} direction=capture error=\"{ex.Message}\"");
            }
        }
        finally
        {
            ExitCallback();
        }
    }

    private int HandleNativePlayback(IntPtr samples, int frameCount, int channels)
    {
        // Fix #10: Guard against callbacks arriving after disposal.
        if (_disposed || samples == IntPtr.Zero || frameCount <= 0 || channels <= 0) return 0;
        EnterCallback();
        try
        {
            Interlocked.Increment(ref _playbackCallbacks);
            Interlocked.Add(ref _playbackFrames, frameCount);
            var sampleCount = checked(frameCount * channels);
            try
            {
                EnsureScratch(ref _playbackScratch, sampleCount);
                Array.Clear(_playbackScratch, 0, sampleCount);
                var read = _running ? _playbackSource?.Invoke(_playbackScratch, sampleCount) ?? 0 : 0;
                if (read < sampleCount)
                {
                    Interlocked.Increment(ref _underruns);
                    if (read > 0)
                        Array.Clear(_playbackScratch, read, sampleCount - read);
                }
                Marshal.Copy(_playbackScratch, 0, samples, sampleCount);
                return sampleCount;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _callbackErrors);
                Interlocked.Increment(ref _underruns);
                Array.Clear(_playbackScratch, 0, Math.Min(_playbackScratch.Length, sampleCount));
                Marshal.Copy(_playbackScratch, 0, samples, sampleCount);
                VoiceDiagnostics.Log("mac.audio.callback_error",
                    $"backend={_owner} direction=playback error=\"{ex.Message}\"");
                return sampleCount;
            }
        }
        finally
        {
            ExitCallback();
        }
    }

    private static void EnsureScratch(ref float[] buffer, int count)
    {
        if (buffer.Length < count)
            buffer = new float[count];
    }
}
#endif