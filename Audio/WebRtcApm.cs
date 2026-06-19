#if WINDOWS
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;

namespace VoiceChatPlugin.Audio;

internal sealed unsafe class WebRtcApm : IDisposable
{
    private const string NativeFileName = "webrtc-apm.dll";
    private static string ResourceName => Environment.Is64BitProcess ? "Lib.webrtc-apm.x64.dll" : "Lib.webrtc-apm.x86.dll";
    private static string ArchitectureLabel => Environment.Is64BitProcess ? "x64" : "x86";

    private const int SampleRate = 48000;
    private const int Channels = 1;

    private static readonly object LoadLock = new();
    private static NativeApi? _api;
    private static IntPtr _nativeHandle;
    private static string? _loadError;

    private readonly NativeApi _native;
    private readonly object _captureLock = new();
    private readonly object _reverseLock = new();
    private readonly int _chunk;
    private readonly float[] _captureIn;
    private readonly float[] _captureOut;
    private readonly float[] _reverseIn;
    private IntPtr _apm;
    private IntPtr _streamConfig;
    private bool _disposed;

    private WebRtcApm(NativeApi native, IntPtr apm, IntPtr streamConfig, int chunk)
    {
        _native = native;
        _apm = apm;
        _streamConfig = streamConfig;
        _chunk = chunk;
        _captureIn = new float[chunk];
        _captureOut = new float[chunk];
        _reverseIn = new float[chunk];
    }

    public int ChunkSize => _chunk;
    public string NativePath => _native.NativePath;

    public static bool TryCreate(bool echoCancel, bool gainController2, bool highPass, out WebRtcApm? apm, out string error)
    {
        apm = null;
        if (!TryLoadNative(out var native, out error))
            return false;

        var handle = native.Create();
        if (handle == IntPtr.Zero) { error = "apm-create-failed"; return false; }

        int chunk;
        try { chunk = (int)native.GetFrameSize(SampleRate).ToUInt32(); }
        catch (Exception ex) { native.Destroy(handle); error = $"frame-size:{ex.Message}"; return false; }
        if (chunk <= 0 || AudioHelpers.FrameSize % chunk != 0)
        {
            native.Destroy(handle);
            error = $"unsupported-frame-size:{chunk}";
            return false;
        }

        var config = native.ConfigCreate();
        if (config == IntPtr.Zero) { native.Destroy(handle); error = "config-create-failed"; return false; }
        try
        {
            native.ConfigSetEchoCanceller(config, echoCancel ? 1 : 0, 0);
            native.ConfigSetNoiseSuppression(config, 0, 0);
            native.ConfigSetGainController1(config, 0, 0, 0, 0, 0);
            native.ConfigSetGainController2(config, gainController2 ? 1 : 0);
            native.ConfigSetHighPassFilter(config, highPass ? 1 : 0);
            var applyErr = native.ConfigApply(handle, config);
            if (applyErr != 0) { native.Destroy(handle); error = $"apply-config:{applyErr}"; return false; }
        }
        finally { native.ConfigDestroy(config); }

        var initErr = native.Initialize(handle);
        if (initErr != 0) { native.Destroy(handle); error = $"init:{initErr}"; return false; }

        var streamConfig = native.StreamConfigCreate(SampleRate, (UIntPtr)Channels);
        if (streamConfig == IntPtr.Zero) { native.Destroy(handle); error = "stream-config-failed"; return false; }

        apm = new WebRtcApm(native, handle, streamConfig, chunk);
        error = string.Empty;
        return true;
    }

    public void ProcessCapture(float[] pcm, int sampleCount)
    {
        lock (_captureLock)
        {
            if (_disposed || _apm == IntPtr.Zero) return;
            var count = Math.Min(sampleCount, pcm.Length);
            var off = 0;
            while (off + _chunk <= count)
            {
                Array.Copy(pcm, off, _captureIn, 0, _chunk);
                fixed (float* inPtr = _captureIn)
                fixed (float* outPtr = _captureOut)
                {
                    float* inChan = inPtr;
                    float* outChan = outPtr;
                    _native.ProcessStream(_apm, (IntPtr)(&inChan), _streamConfig, _streamConfig, (IntPtr)(&outChan));
                }
                Array.Copy(_captureOut, 0, pcm, off, _chunk);
                off += _chunk;
            }
        }
    }

    public void ProcessReverse(float[] pcm, int sampleCount)
    {
        lock (_reverseLock)
        {
            if (_disposed || _apm == IntPtr.Zero) return;
            var count = Math.Min(sampleCount, pcm.Length);
            var off = 0;
            while (off + _chunk <= count)
            {
                Array.Copy(pcm, off, _reverseIn, 0, _chunk);
                fixed (float* revPtr = _reverseIn)
                {
                    float* revChan = revPtr;
                    _native.AnalyzeReverseStream(_apm, (IntPtr)(&revChan), _streamConfig);
                }
                off += _chunk;
            }
        }
    }

    public void SetStreamDelayMs(int delayMs)
    {
        lock (_captureLock)
        {
            if (_disposed || _apm == IntPtr.Zero) return;
            _native.SetStreamDelayMs(_apm, delayMs < 0 ? 0 : delayMs);
        }
    }

    public void Dispose()
    {
        lock (_captureLock)
        lock (_reverseLock)
        {
            if (_disposed) return;
            _disposed = true;
            var sc = _streamConfig;
            var apm = _apm;
            _streamConfig = IntPtr.Zero;
            _apm = IntPtr.Zero;
            if (sc != IntPtr.Zero) _native.StreamConfigDestroy(sc);
            if (apm != IntPtr.Zero) _native.Destroy(apm);
        }
    }

    public static bool SmokeTest(out string detail)
    {
        if (!TryCreate(true, true, true, out var apm, out var error) || apm == null)
        {
            detail = error;
            return false;
        }
        try
        {
            var frame = new float[AudioHelpers.FrameSize];
            apm.ProcessReverse(frame, frame.Length);
            apm.SetStreamDelayMs(60);
            apm.ProcessCapture(frame, frame.Length);
            detail = $"ok chunk={apm.ChunkSize}";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
        finally { apm.Dispose(); }
    }

    private static bool TryLoadNative(out NativeApi native, out string error)
    {
        lock (LoadLock)
        {
            if (_api != null) { native = _api; error = string.Empty; return true; }
            if (!string.IsNullOrEmpty(_loadError)) { native = default!; error = _loadError; return false; }

            try
            {
                var path = ExtractNativeLibrary();
                _nativeHandle = NativeLibrary.Load(path);
                _api = new NativeApi(
                    GetExport<ApmCreate>("webrtc_apm_create"),
                    GetExport<ApmDestroy>("webrtc_apm_destroy"),
                    GetExport<ApmConfigCreate>("webrtc_apm_config_create"),
                    GetExport<ApmConfigDestroy>("webrtc_apm_config_destroy"),
                    GetExport<ApmConfigSetEchoCanceller>("webrtc_apm_config_set_echo_canceller"),
                    GetExport<ApmConfigSetNoiseSuppression>("webrtc_apm_config_set_noise_suppression"),
                    GetExport<ApmConfigSetGainController1>("webrtc_apm_config_set_gain_controller1"),
                    GetExport<ApmConfigSetGainController2>("webrtc_apm_config_set_gain_controller2"),
                    GetExport<ApmConfigSetHighPassFilter>("webrtc_apm_config_set_high_pass_filter"),
                    GetExport<ApmConfigApply>("webrtc_apm_apply_config"),
                    GetExport<ApmInitialize>("webrtc_apm_initialize"),
                    GetExport<ApmStreamConfigCreate>("webrtc_apm_stream_config_create"),
                    GetExport<ApmStreamConfigDestroy>("webrtc_apm_stream_config_destroy"),
                    GetExport<ApmProcessStream>("webrtc_apm_process_stream"),
                    GetExport<ApmAnalyzeReverseStream>("webrtc_apm_analyze_reverse_stream"),
                    GetExport<ApmSetStreamDelayMs>("webrtc_apm_set_stream_delay_ms"),
                    GetExport<ApmGetFrameSize>("webrtc_apm_get_frame_size"),
                    path);
                native = _api;
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                native = default!;
                error = _loadError;
                return false;
            }
        }
    }

    private static T GetExport<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_nativeHandle, name));

    private static string ExtractNativeLibrary()
        => NativeLibraryCache.Extract(Assembly.GetExecutingAssembly(), ResourceName, NativeFileName, ArchitectureLabel, ResolveBaseDirectory());

    private static string ResolveBaseDirectory()
    {
        try
        {
            var root = ProbeBepInExRoot();
            if (!string.IsNullOrWhiteSpace(root)) return root;
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ProbeBepInExRoot() => Paths.BepInExRootPath;

    private sealed class NativeApi
    {
        public readonly ApmCreate Create;
        public readonly ApmDestroy Destroy;
        public readonly ApmConfigCreate ConfigCreate;
        public readonly ApmConfigDestroy ConfigDestroy;
        public readonly ApmConfigSetEchoCanceller ConfigSetEchoCanceller;
        public readonly ApmConfigSetNoiseSuppression ConfigSetNoiseSuppression;
        public readonly ApmConfigSetGainController1 ConfigSetGainController1;
        public readonly ApmConfigSetGainController2 ConfigSetGainController2;
        public readonly ApmConfigSetHighPassFilter ConfigSetHighPassFilter;
        public readonly ApmConfigApply ConfigApply;
        public readonly ApmInitialize Initialize;
        public readonly ApmStreamConfigCreate StreamConfigCreate;
        public readonly ApmStreamConfigDestroy StreamConfigDestroy;
        public readonly ApmProcessStream ProcessStream;
        public readonly ApmAnalyzeReverseStream AnalyzeReverseStream;
        public readonly ApmSetStreamDelayMs SetStreamDelayMs;
        public readonly ApmGetFrameSize GetFrameSize;
        public readonly string NativePath;

        public NativeApi(
            ApmCreate create, ApmDestroy destroy, ApmConfigCreate configCreate, ApmConfigDestroy configDestroy,
            ApmConfigSetEchoCanceller setEcho, ApmConfigSetNoiseSuppression setNs, ApmConfigSetGainController1 setAgc1,
            ApmConfigSetGainController2 setAgc2, ApmConfigSetHighPassFilter setHpf, ApmConfigApply configApply,
            ApmInitialize initialize, ApmStreamConfigCreate streamConfigCreate, ApmStreamConfigDestroy streamConfigDestroy,
            ApmProcessStream processStream, ApmAnalyzeReverseStream analyzeReverse, ApmSetStreamDelayMs setDelay,
            ApmGetFrameSize getFrameSize, string nativePath)
        {
            Create = create;
            Destroy = destroy;
            ConfigCreate = configCreate;
            ConfigDestroy = configDestroy;
            ConfigSetEchoCanceller = setEcho;
            ConfigSetNoiseSuppression = setNs;
            ConfigSetGainController1 = setAgc1;
            ConfigSetGainController2 = setAgc2;
            ConfigSetHighPassFilter = setHpf;
            ConfigApply = configApply;
            Initialize = initialize;
            StreamConfigCreate = streamConfigCreate;
            StreamConfigDestroy = streamConfigDestroy;
            ProcessStream = processStream;
            AnalyzeReverseStream = analyzeReverse;
            SetStreamDelayMs = setDelay;
            GetFrameSize = getFrameSize;
            NativePath = nativePath;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ApmCreate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmDestroy(IntPtr apm);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ApmConfigCreate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmConfigDestroy(IntPtr config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmConfigSetEchoCanceller(IntPtr config, int enabled, int mobileMode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmConfigSetNoiseSuppression(IntPtr config, int enabled, int level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmConfigSetGainController1(IntPtr config, int enabled, int mode, int targetLevelDbfs, int compressionGainDb, int enableLimiter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmConfigSetGainController2(IntPtr config, int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmConfigSetHighPassFilter(IntPtr config, int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ApmConfigApply(IntPtr apm, IntPtr config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ApmInitialize(IntPtr apm);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ApmStreamConfigCreate(int sampleRateHz, UIntPtr numChannels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmStreamConfigDestroy(IntPtr config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ApmProcessStream(IntPtr apm, IntPtr src, IntPtr inputConfig, IntPtr outputConfig, IntPtr dest);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ApmAnalyzeReverseStream(IntPtr apm, IntPtr data, IntPtr reverseConfig);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApmSetStreamDelayMs(IntPtr apm, int delayMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UIntPtr ApmGetFrameSize(int sampleRateHz);
}
#endif
