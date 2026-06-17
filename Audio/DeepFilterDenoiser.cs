#if WINDOWS
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;

namespace VoiceChatPlugin.Audio;

// DeepFilterNet 3 neural noise suppressor via native libdf (df.dll, win-x64, with the DFN3 model embedded).
// Drop-in replacement for RnNoiseSuppressor on Windows. Processes normalized float [-1,1] in hop_size chunks;
// df_process_frame is a streaming filter (internal lookahead), so per-chunk in-place processing is correct.
internal sealed unsafe class DeepFilterDenoiser : INoiseSuppressor
{
    private const string NativeFileName = "df.dll";
    private static string ResourceName => Environment.Is64BitProcess ? "Lib.df.x64.dll" : "Lib.df.x86.dll";
    private static string ArchitectureLabel => Environment.Is64BitProcess ? "x64" : "x86";
    private const float AttenLimitDb = 100f; // upper bound on attenuation; the model decides how much to remove

    private static readonly object LoadLock = new();
    private static NativeApi? _api;
    private static IntPtr _nativeHandle;
    private static string? _loadError;

    private readonly NativeApi _native;
    private readonly int _frameSize;
    private readonly float[] _input;
    private readonly float[] _output;
    private readonly object _stateLock = new();
    private IntPtr _state;
    private bool _disposed;

    private DeepFilterDenoiser(NativeApi native, IntPtr state, int frameSize)
    {
        _native = native;
        _state = state;
        _frameSize = frameSize;
        _input = new float[frameSize];
        _output = new float[frameSize];
    }

    public int FrameSize => _frameSize;
    public string NativePath => _native.NativePath;

    public static bool TryCreate(out DeepFilterDenoiser? suppressor, out string error)
    {
        suppressor = null;
        if (!TryLoadNative(out var native, out error))
            return false;

        var state = native.Create(AttenLimitDb);
        if (state == IntPtr.Zero) { error = "create-failed"; return false; }

        int frameSize;
        try { frameSize = (int)native.GetFrameLength(state).ToUInt32(); }
        catch (Exception ex) { native.Free(state); error = $"frame-size:{ex.Message}"; return false; }

        if (frameSize <= 0 || AudioHelpers.FrameSize % frameSize != 0)
        {
            native.Free(state);
            error = $"unsupported-frame-size:{frameSize}";
            return false;
        }

        suppressor = new DeepFilterDenoiser(native, state, frameSize);
        error = string.Empty;
        return true;
    }

    public bool TryProcessInPlace(float[] pcm, int sampleCount, out int processedFrames, out float speechProbabilityMax)
    {
        processedFrames = 0;
        speechProbabilityMax = 0f;
        lock (_stateLock)
        {
            if (_disposed || _state == IntPtr.Zero) return false;

            var count = Math.Min(sampleCount, pcm.Length);
            var processed = 0;
            var maxSnr = float.NegativeInfinity;
            while (processed + _frameSize <= count)
            {
                for (var i = 0; i < _frameSize; i++)
                    _input[i] = Math.Clamp(pcm[processed + i], -1f, 1f);

                float snr;
                fixed (float* input = _input)
                fixed (float* output = _output)
                    snr = _native.ProcessFrame(_state, input, output);

                for (var i = 0; i < _frameSize; i++)
                    pcm[processed + i] = Math.Clamp(_output[i], -1f, 1f);

                if (snr > maxSnr) maxSnr = snr;
                processed += _frameSize;
                processedFrames++;
            }

            if (processed > 0)
                speechProbabilityMax = Math.Clamp((maxSnr + 20f) / 60f, 0f, 1f); // rough presence proxy from local SNR (dB)
            return processed > 0;
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            var replacement = _native.Create(AttenLimitDb);
            if (replacement == IntPtr.Zero) return;
            var previous = _state;
            _state = replacement;
            if (previous != IntPtr.Zero)
                _native.Free(previous);
        }
    }

    public void Dispose()
    {
        DisposeNative();
        GC.SuppressFinalize(this);
    }

    ~DeepFilterDenoiser() => DisposeNative();

    private void DisposeNative()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            var state = _state;
            _state = IntPtr.Zero;
            if (state != IntPtr.Zero)
                _native.Free(state);
        }
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
                    GetExport<DfCreate>("df_create"),
                    GetExport<DfGetFrameLength>("df_get_frame_length"),
                    GetExport<DfProcessFrame>("df_process_frame"),
                    GetExport<DfFree>("df_free"),
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

    // df_create takes a model PATH; an empty string selects the model embedded in df.dll (capi patched to
    // fall back to DfParams::default() on an empty path). log_level NULL => no logging.
    private sealed class NativeApi
    {
        private static readonly IntPtr EmptyPath = Marshal.StringToHGlobalAnsi(string.Empty);
        private readonly DfCreate _create;
        public readonly DfGetFrameLength GetFrameLength;
        public readonly DfProcessFrame ProcessFrame;
        public readonly DfFree Free;
        public readonly string NativePath;

        public NativeApi(DfCreate create, DfGetFrameLength getFrameLength, DfProcessFrame processFrame, DfFree free, string nativePath)
        {
            _create = create;
            GetFrameLength = getFrameLength;
            ProcessFrame = processFrame;
            Free = free;
            NativePath = nativePath;
        }

        public IntPtr Create(float attenLimDb) => _create(EmptyPath, attenLimDb, IntPtr.Zero);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DfCreate(IntPtr path, float attenLim, IntPtr logLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UIntPtr DfGetFrameLength(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float DfProcessFrame(IntPtr state, float* input, float* output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DfFree(IntPtr state);
}
#endif
