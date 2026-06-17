using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace VoiceChatPlugin;

internal static class NativeLibraryCache
{
    public static string Extract(Assembly assembly, string resourceName, string fileName, string archLabel, string baseDirectory)
    {
        var dir = Path.Combine(baseDirectory, "cache", "PerfectComms", "native", archLabel);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, fileName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Missing embedded resource {resourceName}");

        using var sha = SHA256.Create();
        var expected = sha.ComputeHash(stream);
        if (File.Exists(target) && FileHashMatches(target, expected))
            return target;

        stream.Position = 0;
        var temp = $"{target}.{Environment.ProcessId}.tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.CopyTo(output);

        try
        {
            File.Move(temp, target, true);
        }
        catch (IOException)
        {
            if (!(File.Exists(target) && FileHashMatches(target, expected)))
                throw;
            try { File.Delete(temp); } catch { }
        }

        return target;
    }

    private static bool FileHashMatches(string path, byte[] expected)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return sha.ComputeHash(fs).AsSpan().SequenceEqual(expected);
    }
}
