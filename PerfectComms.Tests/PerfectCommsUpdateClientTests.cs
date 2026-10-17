using System.Runtime.InteropServices;
using VoiceChatPlugin;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class PerfectCommsUpdateClientTests
{
    [Theory]
    [InlineData("win", Architecture.X64, "PerfectComms-win.dll")]
    [InlineData("linux", Architecture.X64, "PerfectComms-linux.dll")]
    [InlineData("mac", Architecture.X64, "PerfectComms-mac.dll")]
    [InlineData("mac", Architecture.Arm64, "PerfectComms-mac.dll")]
    [InlineData("linux", Architecture.Arm64, null)]
    public void ReleaseAssetNameMatchesSupportedTarget(
        string targetOs,
        Architecture architecture,
        string? expected)
    {
        Assert.Equal(expected, PerfectCommsUpdateClient.ReleaseAssetNameFor(targetOs, architecture));
    }

    [Theory]
    [InlineData(true, "Linux", false, false, false, "linux")]
    [InlineData(true, "MacOS", false, false, false, "mac")]
    [InlineData(true, "Unknown", true, false, false, null)]
    [InlineData(false, "Unknown", true, false, false, "win")]
    [InlineData(false, "Unknown", false, true, false, "linux")]
    [InlineData(false, "Unknown", false, false, true, "mac")]
    public void RuntimeTargetMatchesNativeHelperSelection(
        bool wine,
        string wineHostOs,
        bool windows,
        bool linux,
        bool macOs,
        string? expected)
    {
        Assert.Equal(
            expected,
            PerfectCommsUpdateClient.TargetOsFor(
                wine,
                Enum.Parse<WineHostOs>(wineHostOs),
                windows,
                linux,
                macOs));
    }

    [Fact]
    public void GithubReleaseLinksToMatchingPlatformDll()
    {
        const string release = """
            {
              "tag_name": "v4.1.12",
              "html_url": "https://github.com/artriy/Perfect-Comms/releases/tag/v4.1.12",
              "assets": [
                { "name": "PerfectComms-win.dll", "browser_download_url": "https://example.test/win.dll" },
                { "name": "PerfectComms-linux.dll", "browser_download_url": "https://example.test/linux.dll" },
                { "name": "PerfectComms-mac.dll", "browser_download_url": "https://example.test/mac.dll" }
              ]
            }
            """;

        var info = PerfectCommsUpdateClient.ParseUpdateInfo(release, "linux", Architecture.X64);

        Assert.NotNull(info);
        Assert.Equal("https://example.test/linux.dll", info.ReleaseUrl);
    }

    [Fact]
    public void GithubReleaseFallsBackToReleasePageWhenTargetAssetIsUnavailable()
    {
        const string release = """
            {
              "tag_name": "v4.1.12",
              "html_url": "https://github.com/artriy/Perfect-Comms/releases/tag/v4.1.12",
              "assets": []
            }
            """;

        var info = PerfectCommsUpdateClient.ParseUpdateInfo(release, "linux", Architecture.X64);

        Assert.NotNull(info);
        Assert.Equal("https://github.com/artriy/Perfect-Comms/releases/tag/v4.1.12", info.ReleaseUrl);
    }
}
