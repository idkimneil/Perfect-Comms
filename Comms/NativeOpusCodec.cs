#if WINDOWS
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

// P/Invoke surface for native libopus 1.6.1 (built with deep PLC + DRED + OSCE; shipped as Lib.opus.x64.dll).
// Loaded once per process, mirroring RnNoiseSuppressor's extract-and-load template.
internal static unsafe class OpusNative
{
    public const int OPUS_OK = 0;
    public const int OPUS_APPLICATION_VOIP = 2048;
    public const int OPUS_SIGNAL_VOICE = 3001;
    public const int OPUS_SIGNAL_AUTO = -1000;
    public const int OPUS_SET_BITRATE = 4002;
    public const int OPUS_SET_VBR = 4006;
    public const int OPUS_SET_COMPLEXITY = 4010;
    public const int OPUS_SET_INBAND_FEC = 4012;
    public const int OPUS_SET_PACKET_LOSS_PERC = 4014;
    public const int OPUS_SET_DTX = 4016;
    public const int OPUS_SET_VBR_CONSTRAINT = 4020;
    public const int OPUS_SET_SIGNAL = 4024;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr EncoderCreate(int fs, int channels, int application, out int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int EncoderCtl(IntPtr st, int request, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Encode(IntPtr st, short* pcm, int frameSize, byte* data, int maxData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EncoderDestroy(IntPtr st);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr DecoderCreate(int fs, int channels, out int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Decode(IntPtr st, byte* data, int len, short* pcm, int frameSize, int decodeFec);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int DecodeFloat(IntPtr st, byte* data, int len, float* pcm, int frameSize, int decodeFec);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DecoderDestroy(IntPtr st);

    public static EncoderCreate OpusEncoderCreate { get; private set; } = null!;
    public static EncoderCtl OpusEncoderCtl { get; private set; } = null!;
    public static Encode OpusEncode { get; private set; } = null!;
    public static EncoderDestroy OpusEncoderDestroy { get; private set; } = null!;
    public static DecoderCreate OpusDecoderCreate { get; private set; } = null!;
    public static Decode OpusDecode { get; private set; } = null!;
    public static DecodeFloat OpusDecodeFloat { get; private set; } = null!;
    public static DecoderDestroy OpusDecoderDestroy { get; private set; } = null!;

    private const string NativeFileName = "opus.dll";
    private const string ResourceName = "Lib.opus.x64.dll";
    private static readonly object LoadLock = new();
    private static bool _loaded;
    private static IntPtr _handle;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (LoadLock)
        {
            if (_loaded) return;
            var path = ExtractNativeLibrary();
            _handle = NativeLibrary.Load(path);
            OpusEncoderCreate = Export<EncoderCreate>("opus_encoder_create");
            OpusEncoderCtl = Export<EncoderCtl>("opus_encoder_ctl");
            OpusEncode = Export<Encode>("opus_encode");
            OpusEncoderDestroy = Export<EncoderDestroy>("opus_encoder_destroy");
            OpusDecoderCreate = Export<DecoderCreate>("opus_decoder_create");
            OpusDecode = Export<Decode>("opus_decode");
            OpusDecodeFloat = Export<DecodeFloat>("opus_decode_float");
            OpusDecoderDestroy = Export<DecoderDestroy>("opus_decoder_destroy");
            _loaded = true;
        }
    }

    private static T Export<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_handle, name));

    private static string ExtractNativeLibrary()
    {
        var dir = Path.Combine(ResolveBaseDirectory(), "cache", "PerfectComms", "native", "x64");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, NativeFileName);

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"Missing embedded resource {ResourceName}");

        if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
            return target;

        var temp = $"{target}.{Environment.ProcessId}.tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.CopyTo(output);
        try { File.Move(temp, target, true); }
        catch (IOException)
        {
            if (!(File.Exists(target) && new FileInfo(target).Length == stream.Length)) throw;
            try { File.Delete(temp); } catch { }
        }
        return target;
    }

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
}

internal sealed unsafe class NativeOpusEncoder : IVoiceEncoder
{
    private readonly object _gate = new();
    private IntPtr _enc;

    public NativeOpusEncoder(int bitrate, int complexity, bool voiceSignal, bool vbr, bool constrainedVbr, bool dtx, bool fec, int packetLossPercent)
    {
        OpusNative.EnsureLoaded();
        _enc = OpusNative.OpusEncoderCreate(AudioHelpers.ClockRate, AudioHelpers.Channels, OpusNative.OPUS_APPLICATION_VOIP, out var err);
        if (_enc == IntPtr.Zero || err != OpusNative.OPUS_OK)
            throw new InvalidOperationException($"opus_encoder_create failed: {err}");
        Ctl(OpusNative.OPUS_SET_BITRATE, bitrate);
        Ctl(OpusNative.OPUS_SET_COMPLEXITY, complexity);
        Ctl(OpusNative.OPUS_SET_SIGNAL, voiceSignal ? OpusNative.OPUS_SIGNAL_VOICE : OpusNative.OPUS_SIGNAL_AUTO);
        Ctl(OpusNative.OPUS_SET_VBR, vbr ? 1 : 0);
        Ctl(OpusNative.OPUS_SET_VBR_CONSTRAINT, constrainedVbr ? 1 : 0);
        Ctl(OpusNative.OPUS_SET_DTX, dtx ? 1 : 0);
        Ctl(OpusNative.OPUS_SET_INBAND_FEC, fec ? 1 : 0);
        Ctl(OpusNative.OPUS_SET_PACKET_LOSS_PERC, packetLossPercent);
    }

    private void Ctl(int request, int value)
    {
        if (_enc != IntPtr.Zero)
            OpusNative.OpusEncoderCtl(_enc, request, value);
    }

    public int Bitrate { set { lock (_gate) Ctl(OpusNative.OPUS_SET_BITRATE, value); } }
    public int PacketLossPercent { set { lock (_gate) Ctl(OpusNative.OPUS_SET_PACKET_LOSS_PERC, value); } }

    public int Encode(short[] pcm, int offset, int samples, byte[] packet, int packetOffset, int maxData)
    {
        if (samples <= 0 || maxData <= 0) return 0;
        lock (_gate)
        {
            if (_enc == IntPtr.Zero) return 0;
            fixed (short* p = &pcm[offset])
            fixed (byte* d = &packet[packetOffset])
                return OpusNative.OpusEncode(_enc, p, samples, d, maxData);
        }
    }

    public int Encode(ReadOnlySpan<short> pcm, int frameSize, Span<byte> data, int maxData)
    {
        if (frameSize <= 0 || maxData <= 0) return 0;
        lock (_gate)
        {
            if (_enc == IntPtr.Zero) return 0;
            fixed (short* p = pcm)
            fixed (byte* d = data)
                return OpusNative.OpusEncode(_enc, p, frameSize, d, maxData);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            var e = _enc;
            _enc = IntPtr.Zero;
            if (e != IntPtr.Zero)
                OpusNative.OpusEncoderDestroy(e);
        }
    }
}

internal sealed unsafe class NativeOpusDecoder : IVoiceDecoder
{
    private readonly object _gate = new();
    private IntPtr _dec;

    public NativeOpusDecoder()
    {
        OpusNative.EnsureLoaded();
        _dec = OpusNative.OpusDecoderCreate(AudioHelpers.ClockRate, AudioHelpers.Channels, out var err);
        if (_dec == IntPtr.Zero || err != OpusNative.OPUS_OK)
            throw new InvalidOperationException($"opus_decoder_create failed: {err}");
    }

    // Empty data => packet-loss concealment; native libopus 1.6.1 uses neural deep PLC to synthesize the gap.
    public int Decode(ReadOnlySpan<byte> data, Span<short> pcm, int frameSize, bool fec)
    {
        if (frameSize <= 0) return 0;
        lock (_gate)
        {
            if (_dec == IntPtr.Zero) return 0;
            fixed (short* o = pcm)
            {
                if (data.IsEmpty)
                    return OpusNative.OpusDecode(_dec, null, 0, o, frameSize, fec ? 1 : 0);
                fixed (byte* d = data)
                    return OpusNative.OpusDecode(_dec, d, data.Length, o, frameSize, fec ? 1 : 0);
            }
        }
    }

    public int Decode(byte[] packet, float[] pcm, int frameSize, bool fec)
    {
        if (frameSize <= 0) return 0;
        lock (_gate)
        {
            if (_dec == IntPtr.Zero) return 0;
            fixed (float* o = pcm)
            {
                if (packet == null || packet.Length == 0)
                    return OpusNative.OpusDecodeFloat(_dec, null, 0, o, frameSize, fec ? 1 : 0);
                fixed (byte* d = packet)
                    return OpusNative.OpusDecodeFloat(_dec, d, packet.Length, o, frameSize, fec ? 1 : 0);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            var d = _dec;
            _dec = IntPtr.Zero;
            if (d != IntPtr.Zero)
                OpusNative.OpusDecoderDestroy(d);
        }
    }
}
#endif
