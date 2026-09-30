using octo_fiesta.Models.Settings;
using SixLabors.ImageSharp.PixelFormats;

namespace octo_fiesta.Tests;

public class ExternalCoverSettingsTests
{
    [Theory]
    [InlineData("0", ExternalCoverIndicatorColorKind.Frost)]
    [InlineData("1", ExternalCoverIndicatorColorKind.Invert)]
    [InlineData("ffffff", ExternalCoverIndicatorColorKind.CustomFill)]
    [InlineData("00FF00", ExternalCoverIndicatorColorKind.CustomFill)]
    [InlineData("f00", ExternalCoverIndicatorColorKind.CustomFill)]
    public void ResolveIndicatorColor_ValidValues(string raw, ExternalCoverIndicatorColorKind expectedKind)
    {
        Assert.True(ExternalCoverSettings.TryResolveIndicatorColor(raw, out var color));
        Assert.Equal(expectedKind, color.Kind);
    }

    [Fact]
    public void ResolveIndicatorColor_Hex_UsesCustomFillAlpha()
    {
        Assert.True(ExternalCoverSettings.TryResolveIndicatorColor("00ff00", out var color));
        Assert.Equal(ExternalCoverIndicatorColorKind.CustomFill, color.Kind);
        Assert.Equal(new Rgba32(0, 255, 0, ExternalCoverSettings.CustomFillAlpha), color.FillTint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("zzzzzz")]
    [InlineData("12345")]
    public void ResolveIndicatorColor_InvalidValues_ReturnFalse(string raw)
    {
        Assert.False(ExternalCoverSettings.TryResolveIndicatorColor(raw, out _));
    }

    [Fact]
    public void ResolveIndicatorColor_InvalidValue_FallsBackToFrostInSettings()
    {
        var settings = new ExternalCoverSettings { IndicatorColor = "not-a-color" };
        var color = settings.ResolveIndicatorColor();
        Assert.Equal(ExternalCoverIndicatorColorKind.Frost, color.Kind);
    }

    [Fact]
    public void GetColorCacheKeySegment_CustomHex_IsNormalized()
    {
        Assert.Equal("00FF00", ExternalCoverSettings.GetColorCacheKeySegment("00ff00"));
    }
}
