using System;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin.Audio;

internal readonly record struct MicFrameDecision(
    bool ShouldTransmit,
    float Peak,
    float Rms,
    float Threshold,
    string Reason);

internal readonly record struct NoiseSuppressionDiagnostics(
    string State,
    string LastError,
    string NativePath,
    int FrameSize,
    int Attempts,
    int ProcessedFrames,
    int UnavailableFrames,
    int Samples,
    float InputPeak,
    double InputRms,
    float OutputPeak,
    double OutputRms,
    float SpeechProbabilityMax);

internal sealed class MicPreprocessor : IDisposable
{
    private const float MinimumTransmitGate = 0.0005f;
    private const float AgcTargetPeak = 0.30f;
    private const float AgcMaxGain = 16f;
    private const float AgcSpeechFloor = 0.002f;
    private const float AgcPeakCeiling = 0.9f;
    private const float AgcGainRisePerFrame = 1.02f;
    private const float AgcSpeechPeakDecay = 0.995f;
    private const float AgcSpeechPeakRisePerFrame = 1.10f;
    private const float HighPassCoefficient = 0.98953f;

    public float AutoGainSeedFloor { get; set; } = 0.003f;

    private readonly object _noiseSuppressionStatsLock = new();
    private float _agcGain = 1f;
    private float _agcLastAppliedGain = 1f;
    private float _agcRecentSpeechPeak;
    private float _hpfLastInput;
    private float _hpfLastOutput;
    private bool _disposed;
    private bool _noiseSuppressionEnabled = true;
    private INoiseSuppressor? _noiseSuppressor;
#if WINDOWS
    private WebRtcApm? _apm;
    private string _apmState = "disabled";
    private bool _apmEcho = true;
    private bool _apmGainControl = true;
#endif
    private string _noiseSuppressionState = "disabled";
    private string _noiseSuppressionLastError = "none";
    private string _noiseSuppressionNativePath = string.Empty;
    private int _noiseSuppressionFrameSize;
    private int _noiseSuppressionAttemptsSinceStats;
    private int _noiseSuppressionProcessedFramesSinceStats;
    private int _noiseSuppressionUnavailableFramesSinceStats;
    private int _noiseSuppressionSamplesSinceStats;
    private float _noiseSuppressionInputPeakSinceStats;
    private float _noiseSuppressionOutputPeakSinceStats;
    private double _noiseSuppressionInputSquareSumSinceStats;
    private double _noiseSuppressionOutputSquareSumSinceStats;
    private float _noiseSuppressionSpeechProbabilityMaxSinceStats;

    public void Reset(bool preserveAutoGain = false)
    {
        if (!preserveAutoGain)
            ResetAutoGain();
        _hpfLastInput = 0f;
        _hpfLastOutput = 0f;
        _noiseSuppressor?.Reset();
        ResetEchoCancellation();
    }

    public void ResetAutoGain()
    {
        _agcGain = 1f;
        _agcLastAppliedGain = 1f;
        _agcRecentSpeechPeak = 0f;
    }

    public void ApplyHighPass(float[] pcm, int sampleCount)
    {
        int count = Math.Min(sampleCount, pcm.Length);
        float lastIn = _hpfLastInput;
        float lastOut = _hpfLastOutput;
        for (int i = 0; i < count; i++)
        {
            float input = pcm[i];
            if (!float.IsFinite(input)) input = 0f;
            lastOut = HighPassCoefficient * (lastOut + input - lastIn);
            lastIn = input;
            pcm[i] = lastOut;
        }
        _hpfLastInput = lastIn;
        _hpfLastOutput = lastOut;
    }

    public float ProcessCaptureSample(float sample, float gain) => sample;

    public float ApplyAutoGain(float[] pcm, int sampleCount, bool enabled, out float postGainPeak)
    {
        int count = Math.Min(sampleCount, pcm.Length);
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float sample = pcm[i];
            if (!float.IsFinite(sample))
                continue;

            float abs = sample < 0f ? -sample : sample;
            if (abs > peak) peak = abs;
        }

        if (!enabled || count <= 0)
        {
            float previousDisabledGain = _agcLastAppliedGain;
            _agcGain = 1f;
            _agcLastAppliedGain = 1f;
            _agcRecentSpeechPeak = 0f;
            postGainPeak = peak * Math.Max(previousDisabledGain, 1f);
            if (count > 0 && Math.Abs(previousDisabledGain - 1f) > 0.0001f)
            {
                float step = (1f - previousDisabledGain) / count;
                float rampGain = previousDisabledGain;
                for (int i = 0; i < count; i++)
                {
                    rampGain += step;
                    pcm[i] *= rampGain;
                }
            }
            return 1f;
        }

        float seedFloor = Math.Max(AutoGainSeedFloor, AgcSpeechFloor);
        if (peak >= seedFloor)
        {
            float risenPeak = Math.Min(peak, Math.Max(_agcRecentSpeechPeak * AgcSpeechPeakRisePerFrame, seedFloor));
            _agcRecentSpeechPeak = Math.Max(risenPeak, _agcRecentSpeechPeak * AgcSpeechPeakDecay);
        }

        float gain = 1f;
        if (_agcRecentSpeechPeak >= AgcSpeechFloor)
        {
            gain = Math.Clamp(AgcTargetPeak / _agcRecentSpeechPeak, 1f, AgcMaxGain);
            if (gain > _agcGain)
                gain = Math.Min(gain, _agcGain * AgcGainRisePerFrame);
        }

        if (peak * gain > AgcPeakCeiling)
            gain = AgcPeakCeiling / peak;

        // Ceiling ducking is transient: never let it drag the rise-cap baseline below unity for ~0.5s.
        _agcGain = Math.Max(1f, gain);
        float previousGain = _agcLastAppliedGain;
        _agcLastAppliedGain = gain;
        postGainPeak = peak * Math.Max(previousGain, gain);
        if (gain == 1f && previousGain == 1f)
            return 1f;

        float rampStep = (gain - previousGain) / count;
        float applied = previousGain;
        for (int i = 0; i < count; i++)
        {
            applied += rampStep;
            pcm[i] *= applied;
        }

        return gain;
    }

    public void SetNoiseSuppressionEnabled(bool enabled)
    {
        _noiseSuppressionEnabled = enabled;
        if (enabled)
        {
            if (_noiseSuppressionState == "disabled")
                SetNoiseSuppressionState("enabled-waiting-for-audio", "none", null);
            return;
        }

        _noiseSuppressor?.Dispose();
        _noiseSuppressor = null;
        SetNoiseSuppressionState("disabled", "none", null);
    }

    public bool TryApplyNoiseSuppression(float[] pcm, int sampleCount)
    {
        var count = Math.Min(sampleCount, pcm.Length);
        if (count <= 0) return false;
        // Sticky intent: never lazily resurrect the native suppressor after Dispose or an explicit disable.
        if (_disposed || !_noiseSuppressionEnabled) return false;

        Measure(pcm, count, out var inputPeak, out var inputSquareSum);
        if (_noiseSuppressor == null)
        {
#if WINDOWS
            if (!DeepFilterDenoiser.TryCreate(out var suppressor, out var error))
            {
                SetNoiseSuppressionState("unavailable", error, null);
                TrackNoiseSuppressionFrame(false, 0, true, count, inputPeak, inputSquareSum, inputPeak, inputSquareSum, 0f);
                return false;
            }

            _noiseSuppressor = suppressor;
            SetNoiseSuppressionState("ready", "none", suppressor);
#else
            // No native noise suppressor on non-Windows builds (DeepFilterNet ships win-x64 only).
            SetNoiseSuppressionState("unavailable", "no-native-noise-suppressor", null);
            TrackNoiseSuppressionFrame(false, 0, true, count, inputPeak, inputSquareSum, inputPeak, inputSquareSum, 0f);
            return false;
#endif
        }

        var activeSuppressor = _noiseSuppressor;
        if (activeSuppressor == null)
        {
            TrackNoiseSuppressionFrame(false, 0, true, count, inputPeak, inputSquareSum, inputPeak, inputSquareSum, 0f);
            return false;
        }

        bool processed;
        int processedFrames;
        float speechProbabilityMax;
        try
        {
            processed = activeSuppressor.TryProcessInPlace(pcm, count, out processedFrames, out speechProbabilityMax);
        }
        catch (Exception ex)
        {
            SetNoiseSuppressionState("process-error", ex.Message, activeSuppressor);
            activeSuppressor.Dispose();
            _noiseSuppressor = null;
            TrackNoiseSuppressionFrame(false, 0, true, count, inputPeak, inputSquareSum, inputPeak, inputSquareSum, 0f);
            return false;
        }

        Measure(pcm, count, out var outputPeak, out var outputSquareSum);
        TrackNoiseSuppressionFrame(processed, processedFrames, false, count, inputPeak, inputSquareSum, outputPeak, outputSquareSum, speechProbabilityMax);
        return processed;
    }

    public NoiseSuppressionDiagnostics ConsumeNoiseSuppressionDiagnostics()
    {
        lock (_noiseSuppressionStatsLock)
        {
            var samples = _noiseSuppressionSamplesSinceStats;
            var diagnostics = new NoiseSuppressionDiagnostics(
                _noiseSuppressionState,
                _noiseSuppressionLastError,
                _noiseSuppressionNativePath,
                _noiseSuppressionFrameSize,
                _noiseSuppressionAttemptsSinceStats,
                _noiseSuppressionProcessedFramesSinceStats,
                _noiseSuppressionUnavailableFramesSinceStats,
                samples,
                _noiseSuppressionInputPeakSinceStats,
                samples == 0 ? 0.0 : Math.Sqrt(_noiseSuppressionInputSquareSumSinceStats / samples),
                _noiseSuppressionOutputPeakSinceStats,
                samples == 0 ? 0.0 : Math.Sqrt(_noiseSuppressionOutputSquareSumSinceStats / samples),
                _noiseSuppressionSpeechProbabilityMaxSinceStats);

            _noiseSuppressionAttemptsSinceStats = 0;
            _noiseSuppressionProcessedFramesSinceStats = 0;
            _noiseSuppressionUnavailableFramesSinceStats = 0;
            _noiseSuppressionSamplesSinceStats = 0;
            _noiseSuppressionInputPeakSinceStats = 0f;
            _noiseSuppressionOutputPeakSinceStats = 0f;
            _noiseSuppressionInputSquareSumSinceStats = 0.0;
            _noiseSuppressionOutputSquareSumSinceStats = 0.0;
            _noiseSuppressionSpeechProbabilityMaxSinceStats = 0f;
            return diagnostics;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _noiseSuppressor?.Dispose();
        _noiseSuppressor = null;
#if WINDOWS
        _apm?.Dispose();
        _apm = null;
#endif
    }

    public bool ApmReady =>
#if WINDOWS
        _apm != null;
#else
        false;
#endif

    // (Re)configure the WebRTC AudioProcessingModule used for capture: AEC3 echo cancellation + AGC2 gain,
    // with the high-pass filter always on. Windows only; on other platforms the capture path falls back to
    // the managed high-pass + auto-gain. A failed native load leaves the APM null (graceful fallback).
    public void ConfigureApm(bool echoCancel, bool gainControl2)
    {
#if WINDOWS
        _apmEcho = echoCancel;
        _apmGainControl = gainControl2;
        if (_disposed) return;
        _apm?.Dispose();
        _apm = null;
        if (WebRtcApm.TryCreate(echoCancel, gainControl2, true, out var apm, out var error))
        {
            _apm = apm;
            _apmState = "ready";
        }
        else
        {
            _apmState = $"unavailable:{error}";
        }
        VoiceDiagnostics.Log("bcl.apm", $"state={_apmState} echo={echoCancel} agc2={gainControl2}");
#else
        _ = echoCancel;
        _ = gainControl2;
#endif
    }

    public void DisableApm()
    {
#if WINDOWS
        _apm?.Dispose();
        _apm = null;
        _apmState = "disabled";
#endif
    }

    public void ResetEchoCancellation()
    {
#if WINDOWS
        if (_apm != null) ConfigureApm(_apmEcho, _apmGainControl);
#endif
    }

    // Capture chain on Windows: feed the far-end reference (if available) then run AEC3 + HPF + AGC2 in place.
    // Returns false on non-Windows or when the native APM is unavailable, so the caller applies the managed
    // high-pass + auto-gain fallback instead.
    public bool RunApmCapture(float[] pcm, int sampleCount, float[] reference, bool hasReference, int delayMs)
    {
#if WINDOWS
        var apm = _apm;
        if (_disposed || apm == null) return false;
        if (hasReference)
        {
            apm.SetStreamDelayMs(delayMs);
            apm.ProcessReverse(reference, sampleCount);
        }
        apm.ProcessCapture(pcm, sampleCount);
        return true;
#else
        _ = pcm;
        _ = sampleCount;
        _ = reference;
        _ = hasReference;
        _ = delayMs;
        return false;
#endif
    }

    public string ApmState =>
#if WINDOWS
        _apmState;
#else
        "n/a";
#endif

    public float LimitFramePeakForEncode(float[] pcm, int sampleCount)
    {
        int count = Math.Min(sampleCount, pcm.Length);
        if (count <= 0)
            return 1f;

        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float sample = pcm[i];
            if (!float.IsFinite(sample))
                continue;

            float abs = sample < 0f ? -sample : sample;
            if (abs > peak) peak = abs;
        }

        var gain = AudioHelpers.GetCaptureEncodeLimiterGain(peak);
        if (gain >= 1f)
            return 1f;

        for (int i = 0; i < count; i++)
        {
            if (!float.IsFinite(pcm[i]))
                pcm[i] = 0f;
            else
                pcm[i] *= gain;
        }

        return gain;
    }

    public MicFrameDecision PrepareFrameForEncode(
        float[] pcm,
        int sampleCount,
        float manualGateThreshold,
        float vadThreshold,
        float preSuppressionPeak)
    {
        int count = Math.Min(sampleCount, pcm.Length);
        if (count <= 0)
            return new MicFrameDecision(false, 0f, 0f, 0f, "empty");

        // VAD and gate state are diagnostics/speaking inputs; OpenMic transport stays continuous.
        _ = vadThreshold;
        _ = preSuppressionPeak;
        float peak = 0f;
        double sumSquares = 0.0;
        for (int i = 0; i < count; i++)
        {
            float sample = pcm[i];
            float abs = sample < 0f ? -sample : sample;
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }

        float rms = (float)Math.Sqrt(sumSquares / count);
        float threshold = Math.Max(MinimumTransmitGate, manualGateThreshold);
        return new MicFrameDecision(true, peak, rms, threshold, peak >= threshold ? "voice" : "silence");
    }

    private void TrackNoiseSuppressionFrame(
        bool processed,
        int processedFrames,
        bool unavailable,
        int samples,
        float inputPeak,
        double inputSquareSum,
        float outputPeak,
        double outputSquareSum,
        float speechProbabilityMax)
    {
        lock (_noiseSuppressionStatsLock)
        {
            _noiseSuppressionAttemptsSinceStats++;
            if (processed)
                _noiseSuppressionProcessedFramesSinceStats += processedFrames;
            if (unavailable)
                _noiseSuppressionUnavailableFramesSinceStats++;
            _noiseSuppressionSamplesSinceStats += samples;
            _noiseSuppressionInputPeakSinceStats = Math.Max(_noiseSuppressionInputPeakSinceStats, inputPeak);
            _noiseSuppressionOutputPeakSinceStats = Math.Max(_noiseSuppressionOutputPeakSinceStats, outputPeak);
            _noiseSuppressionInputSquareSumSinceStats += inputSquareSum;
            _noiseSuppressionOutputSquareSumSinceStats += outputSquareSum;
            _noiseSuppressionSpeechProbabilityMaxSinceStats = Math.Max(_noiseSuppressionSpeechProbabilityMaxSinceStats, speechProbabilityMax);
        }
    }

    private void SetNoiseSuppressionState(string state, string error, INoiseSuppressor? suppressor)
    {
        var safeError = string.IsNullOrWhiteSpace(error) ? "none" : SanitizeLogValue(error);
        var nativePath = suppressor?.NativePath ?? _noiseSuppressionNativePath;
        var frameSize = suppressor?.FrameSize ?? _noiseSuppressionFrameSize;
        bool changed;
        lock (_noiseSuppressionStatsLock)
        {
            changed = _noiseSuppressionState != state
                      || _noiseSuppressionLastError != safeError
                      || _noiseSuppressionNativePath != nativePath
                      || _noiseSuppressionFrameSize != frameSize;
            _noiseSuppressionState = state;
            _noiseSuppressionLastError = safeError;
            _noiseSuppressionNativePath = nativePath;
            _noiseSuppressionFrameSize = frameSize;
        }

        if (changed)
            LogNoiseSuppression($"state={state} error=\"{safeError}\" nativePath=\"{SanitizeLogValue(nativePath)}\" frameSize={frameSize}");
    }

    private static void Measure(float[] pcm, int count, out float peak, out double squareSum)
    {
        peak = 0f;
        squareSum = 0.0;
        for (var i = 0; i < count; i++)
        {
            var sample = pcm[i];
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
            squareSum += sample * sample;
        }
    }

    private static void LogNoiseSuppression(string message)
    {
        VoiceDiagnostics.Log("bcl.rnnoise", message);
        try
        {
            global::VoiceChatPlugin.VoiceChatPluginMain.Logger.LogInfo("[VC] bcl.rnnoise " + message);
        }
        catch
        {
        }
    }

    private static string SanitizeLogValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("\"", "'");
}
