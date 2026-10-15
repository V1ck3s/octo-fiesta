using Microsoft.Extensions.Configuration;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

public class DownloadPathResolverTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void GetSharedPath_FallsBackToDefaultWhenUnset()
    {
        var configuration = BuildConfiguration([]);

        Assert.Equal("./downloads", DownloadPathResolver.GetSharedPath(configuration));
    }

    [Fact]
    public void GetProviderPath_PrefersTheProviderOverride()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = "/library",
            // PascalCase, as a Deezer__DownloadPath env var binds, while ProviderName is lowercase.
            ["Deezer:DownloadPath"] = "/music",
        });

        Assert.Equal("/music", DownloadPathResolver.GetProviderPath(configuration, "deezer"));
        Assert.Equal("/library", DownloadPathResolver.GetProviderPath(configuration, "qobuz"));
    }

    [Fact]
    public void GetProviderPath_TreatsAnEmptyOverrideAsAbsent()
    {
        // A compose file passing Deezer__DownloadPath= with no value must not redirect downloads.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = "/library",
            ["Deezer:DownloadPath"] = "",
        });

        Assert.Equal("/library", DownloadPathResolver.GetProviderPath(configuration, "deezer"));
    }

    [Fact]
    public void GetAllRoots_ReturnsSharedPathFirstThenEveryDistinctOverride()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = "/library",
            ["Deezer:DownloadPath"] = "/music",
            ["Qobuz:DownloadPath"] = "/music",
            ["Tidal:DownloadPath"] = "/hires",
            ["Yandex:OAuthToken"] = "token",
            ["SquidWTF:DownloadPath"] = "",
        });

        Assert.Equal(["/library", "/music", "/hires"], DownloadPathResolver.GetAllRoots(configuration));
    }

    [Fact]
    public void GetAllRoots_IsJustTheSharedPathWhenNoProviderOverrides()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = "/library",
            ["Deezer:Arl"] = "arl",
        });

        Assert.Equal(["/library"], DownloadPathResolver.GetAllRoots(configuration));
    }
}
