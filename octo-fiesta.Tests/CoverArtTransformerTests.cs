using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace octo_fiesta.Tests;

public class CoverArtTransformerTests
{
    public static CoverArtTransformer CreateTransformer(ExternalCoverSettings? settings = null) =>
        new(Options.Create(settings ?? new ExternalCoverSettings()));

    [Fact]
    public void ComputeTriangleLeg_Size1_On300pxImage_UsesFeishinRatio()
    {
        using var image = new Image<Rgba32>(300, 300);
        var leg = CoverArtTransformer.ComputeTriangleLeg(image, 1);
        Assert.Equal(60, leg);
    }

    [Fact]
    public void ComputeTriangleLeg_Size2_On300pxImage_DoublesLevelOne()
    {
        using var image = new Image<Rgba32>(300, 300);
        var leg = CoverArtTransformer.ComputeTriangleLeg(image, 2);
        Assert.Equal(120, leg);
    }

    [Fact]
    public void ComputeTriangleLeg_Size0_ReturnsZero()
    {
        using var image = new Image<Rgba32>(300, 300);
        Assert.Equal(0, CoverArtTransformer.ComputeTriangleLeg(image, 0));
    }

    [Fact]
    public async Task ApplyExternalTreatmentAsync_Size0Saturation0_ReturnsUnchangedBytes()
    {
        var source = await CreateSolidJpegAsync(200, 200, new Rgba32(120, 80, 200));
        var transformer = CreateTransformer(new ExternalCoverSettings
        {
            IndicatorSize = 0,
            IndicatorSaturation = 0,
            IndicatorColor = "0"
        });

        var result = await transformer.ApplyExternalTreatmentAsync(source, "image/jpeg");

        Assert.Equal(source, result.Bytes);
    }

    [Fact]
    public async Task ApplyExternalTreatmentAsync_Size1_DrawsIndicator()
    {
        var source = await CreateSolidJpegAsync(300, 300, new Rgba32(120, 80, 200));
        var none = CreateTransformer(new ExternalCoverSettings { IndicatorSize = 0, IndicatorSaturation = 0 });
        var withIndicator = CreateTransformer(new ExternalCoverSettings { IndicatorSize = 1, IndicatorSaturation = 0, IndicatorColor = "0" });

        var without = await none.ApplyExternalTreatmentAsync(source, "image/jpeg");
        var with = await withIndicator.ApplyExternalTreatmentAsync(source, "image/jpeg");

        Assert.NotEqual(without.Bytes, with.Bytes);
        Assert.True(HasCornerDifference(without.Bytes, with.Bytes, 300, 300));
    }

    [Fact]
    public async Task ApplyExternalTreatmentAsync_Saturation1_ReducesColorSpread()
    {
        var source = await CreateGradientPngAsync(200, 200);
        var original = CreateTransformer(new ExternalCoverSettings { IndicatorSize = 0, IndicatorSaturation = 0 });
        var desaturated = CreateTransformer(new ExternalCoverSettings { IndicatorSize = 0, IndicatorSaturation = 1 });

        var originalResult = await original.ApplyExternalTreatmentAsync(source, "image/png");
        var desaturatedResult = await desaturated.ApplyExternalTreatmentAsync(source, "image/png");

        Assert.True(GetChannelSpread(desaturatedResult.Bytes) < GetChannelSpread(originalResult.Bytes));
    }

    [Fact]
    public async Task ApplyExternalTreatmentAsync_Color2_ElevatesRedInCorner()
    {
        var source = await CreateSolidJpegAsync(300, 300, new Rgba32(120, 120, 120));
        var blur = CreateTransformer(new ExternalCoverSettings { IndicatorSize = 1, IndicatorSaturation = 0, IndicatorColor = "0" });
        var green = CreateTransformer(new ExternalCoverSettings { IndicatorSize = 1, IndicatorSaturation = 0, IndicatorColor = "00ff00" });

        var blurResult = await blur.ApplyExternalTreatmentAsync(source, "image/jpeg");
        var greenResult = await green.ApplyExternalTreatmentAsync(source, "image/jpeg");

        Assert.True(GetTopRightAverageGreen(greenResult.Bytes) > GetTopRightAverageGreen(blurResult.Bytes) + 20);
    }

    private static async Task<byte[]> CreateSolidJpegAsync(int width, int height, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height, color);
        await using var stream = new MemoryStream();
        await image.SaveAsJpegAsync(stream);
        return stream.ToArray();
    }

    private static async Task<byte[]> CreateGradientPngAsync(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    row[x] = new Rgba32((byte)x, (byte)y, (byte)((x + y) % 255), 255);
                }
            }
        });

        await using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        return stream.ToArray();
    }

    private static bool HasCornerDifference(byte[] left, byte[] right, int width, int height)
    {
        using var leftImage = Image.Load<Rgba32>(left);
        using var rightImage = Image.Load<Rgba32>(right);

        for (var y = 0; y < 40; y++)
        {
            for (var x = width - 40; x < width; x++)
            {
                if (leftImage[x, y] != rightImage[x, y])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int GetChannelSpread(byte[] bytes)
    {
        using var image = Image.Load<Rgba32>(bytes);
        var minR = 255;
        var maxR = 0;
        var minG = 255;
        var maxG = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                foreach (var pixel in row)
                {
                    minR = Math.Min(minR, pixel.R);
                    maxR = Math.Max(maxR, pixel.R);
                    minG = Math.Min(minG, pixel.G);
                    maxG = Math.Max(maxG, pixel.G);
                }
            }
        });

        return (maxR - minR) + (maxG - minG);
    }

    private static int GetTopRightAverageGreen(byte[] bytes)
    {
        using var image = Image.Load<Rgba32>(bytes);
        long total = 0;
        var count = 0;

        for (var y = 0; y < 40; y++)
        {
            for (var x = image.Width - 40; x < image.Width; x++)
            {
                total += image[x, y].G;
                count++;
            }
        }

        return (int)(total / count);
    }
}
