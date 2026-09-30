using Microsoft.Extensions.Configuration;
using octo_fiesta.Models.Settings;
using Xunit;

namespace octo_fiesta.Tests;

public class MusicServicesSettingTests
{
    [Fact]
    public void ConfigKey_MusicService_BindsTheRawList()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Subsonic:MusicService"] = "Deezer,GDStudio" })
            .Build();

        var settings = config.GetSection("Subsonic").Get<SubsonicSettings>()!;

        Assert.Equal("Deezer,GDStudio", settings.MusicServices);
    }

    [Fact]
    public void Default_IsDeezer()
    {
        Assert.Equal([MusicService.Deezer], SubsonicSettings.ParseMusicServices(new SubsonicSettings().MusicServices, out _));
    }

    [Theory]
    [InlineData("Deezer,GDStudio,AppleMusic")]
    [InlineData(" deezer ; GDSTUDIO | applemusic ")]
    [InlineData("Deezer,GDStudio,AppleMusic,Deezer")]
    public void Parse_HandlesSeparatorsCaseSpacesAndDuplicates(string value)
    {
        var result = SubsonicSettings.ParseMusicServices(value, out var unknown);

        Assert.Equal([MusicService.Deezer, MusicService.GDStudio, MusicService.AppleMusic], result);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Parse_ReportsUnknownEntries()
    {
        var result = SubsonicSettings.ParseMusicServices("GDStudio,Foo", out var unknown);

        Assert.Equal([MusicService.GDStudio], result);
        Assert.Equal(["Foo"], unknown);
    }

    [Fact]
    public void Parse_BlankMeansDeezer()
    {
        Assert.Equal([MusicService.Deezer], SubsonicSettings.ParseMusicServices("  ", out _));
    }
}
