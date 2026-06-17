#if WINDOWS
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;
using ManagedBass;

namespace VoiceChatPlugin.VoiceChat;

internal static class BassRuntime
{
    private static bool _configured;
    private static bool _nativeLoaded;
    private static IntPtr _nativeHandle;
    private static readonly object _sync = new();

    private static string ArchitectureLabel => Environment.Is64BitProcess ? "x64" : "x86";

    public static void EnsureConfigured()
    {
        if (_configured) return;
        lock (_sync)
        {
            if (_configured) return;
            EnsureNativeLoaded();
            Bass.Configure(Configuration.IncludeDefaultDevice, true);
            Bass.Configure(Configuration.PlaybackBufferLength, 180);
            Bass.Configure(Configuration.UpdatePeriod, 10);
            Bass.Configure(Configuration.Algorithm3D, (int)Algorithm3D.Full);
            _configured = true;
        }
    }

    private static void EnsureNativeLoaded()
    {
        if (_nativeLoaded) return;
        _nativeHandle = NativeLibrary.Load(ExtractNativeLibrary($"Lib.bass.{ArchitectureLabel}.dll", "bass.dll"));
        _nativeLoaded = true;
    }

    private static string ExtractNativeLibrary(string resourceName, string fileName)
        => NativeLibraryCache.Extract(Assembly.GetExecutingAssembly(), resourceName, fileName, ArchitectureLabel, ResolveBaseDirectory());

    private static string ResolveBaseDirectory()
    {
        try
        {
            var root = ProbeBepInExRoot();
            if (!string.IsNullOrWhiteSpace(root)) return root;
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ProbeBepInExRoot() => Paths.BepInExRootPath;

    public static int ResolveRecordDevice(string? name)
    {
        EnsureConfigured();
        var requested = (name ?? string.Empty).Trim().ToLowerInvariant();
        var defaultIndex = -1;
        for (var i = 0; i < Bass.RecordingDeviceCount; i++)
        {
            if (!Bass.RecordGetDeviceInfo(i, out var info) || !info.IsEnabled)
                continue;
            if (info.IsDefault)
                defaultIndex = i;
            if (!string.IsNullOrWhiteSpace(requested) && (info.Name ?? string.Empty).Trim().ToLowerInvariant().StartsWith(requested))
                return i;
        }
        return defaultIndex >= 0 ? defaultIndex : 0;
    }

    public static int ResolveOutputDevice(string? name)
    {
        EnsureConfigured();
        var requested = (name ?? string.Empty).Trim().ToLowerInvariant();
        var defaultIndex = -1;
        for (var i = 1; i < Bass.DeviceCount; i++)
        {
            if (!Bass.GetDeviceInfo(i, out var info) || !info.IsEnabled)
                continue;
            if (info.IsDefault)
                defaultIndex = i;
            if (!string.IsNullOrWhiteSpace(requested) && (info.Name ?? string.Empty).Trim().ToLowerInvariant().StartsWith(requested))
                return i;
        }
        return defaultIndex >= 0 ? defaultIndex : -1;
    }

    public static string DescribeOutputDevice(int deviceNumber)
    {
        if (deviceNumber < 0) return "default";
        try { return Bass.GetDeviceInfo(deviceNumber, out var info) ? info.Name : "unknown"; }
        catch { return "unknown"; }
    }

    public static string DescribeOutputDevices()
    {
        EnsureConfigured();
        try
        {
            var names = new System.Collections.Generic.List<string>();
            for (var i = 1; i < Bass.DeviceCount; i++)
                if (Bass.GetDeviceInfo(i, out var info) && info.IsEnabled)
                    names.Add($"{i}:{info.Name}:{(info.IsDefault ? "D" : "-")}");
            return names.Count == 0 ? "none" : string.Join("|", names);
        }
        catch (Exception ex)
        {
            return $"error:{ex.Message}";
        }
    }

    public static bool SmokeTest(out string detail)
    {
        EnsureConfigured();
        if (Bass.Init())
        {
            Bass.Free();
            detail = "ok";
            return true;
        }

        var error = Bass.LastError;
        detail = error.ToString();
        return error == Errors.Already;
    }
}
#endif
