using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace octo_fiesta.Services.Subsonic;

public interface ICoverArtTransformer
{
    Task<CoverArtTransformResult> ApplyExternalTreatmentAsync(byte[] sourceBytes, string contentType, CancellationToken cancellationToken = default);
}

public sealed record CoverArtTransformResult(byte[] Bytes, string ContentType);

public sealed class CoverArtTransformer : ICoverArtTransformer
{
    private const float SaturationLevel1 = 0.4f;
    private const float SaturationLevel2 = 0.16f;
    private const int TriangleFloorPx = 24;
    private const int FeishinVisibleLegPx = 40;
    private const int FeishinCoverReferencePx = 200;
    private const float FeishinLegRatio = (float)FeishinVisibleLegPx / FeishinCoverReferencePx;
    private const int FeishinShadowBlurPx = 10;
    private const int FeishinShadowSpreadPx = 8;

    private readonly ExternalCoverSettings _settings;

    public CoverArtTransformer(IOptions<ExternalCoverSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task<CoverArtTransformResult> ApplyExternalTreatmentAsync(
        byte[] sourceBytes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var sizeLevel = _settings.GetIndicatorSize();
        var saturationLevel = _settings.GetIndicatorSaturation();
        var indicatorColor = _settings.ResolveIndicatorColor();

        if (sizeLevel == 0 && saturationLevel == 0)
        {
            return new CoverArtTransformResult(sourceBytes, contentType);
        }

        var format = Image.DetectFormat(sourceBytes);
        using var image = Image.Load<Rgba32>(sourceBytes);

        ApplySaturation(image, saturationLevel);

        var leg = ComputeTriangleLeg(image, sizeLevel);
        if (leg > 2)
        {
            DrawCornerIndicator(image, leg, indicatorColor);
        }

        await using var output = new MemoryStream();
        var encoder = GetEncoder(contentType, format);
        await image.SaveAsync(output, encoder, cancellationToken);
        return new CoverArtTransformResult(output.ToArray(), GetOutputContentType(contentType, format));
    }

    internal static int ComputeTriangleLeg(Image<Rgba32> image, int sizeLevel)
    {
        if (sizeLevel == 0)
        {
            return 0;
        }

        var shortestSide = Math.Min(image.Width, image.Height);
        var levelOneLeg = Math.Max(TriangleFloorPx, (int)Math.Round(shortestSide * FeishinLegRatio));
        levelOneLeg = Math.Min(levelOneLeg, shortestSide / 2);

        if (sizeLevel == 2)
        {
            return Math.Min(levelOneLeg * 2, shortestSide / 2);
        }

        return levelOneLeg;
    }

    private static void ApplySaturation(Image<Rgba32> image, int saturationLevel)
    {
        var factor = saturationLevel switch
        {
            0 => 1f,
            2 => SaturationLevel2,
            _ => SaturationLevel1
        };

        if (Math.Abs(factor - 1f) < 0.001f)
        {
            return;
        }

        image.Mutate(ctx => ctx.Saturate(factor));
    }

    private static void DrawCornerIndicator(Image<Rgba32> image, int size, ExternalCoverIndicatorColor indicatorColor)
    {
        switch (indicatorColor.Kind)
        {
            case ExternalCoverIndicatorColorKind.Invert:
                DrawCornerTriangle(image, size, useInvert: true);
                break;
            case ExternalCoverIndicatorColorKind.CustomFill:
                DrawCornerTriangle(image, size, useInvert: false, fillTint: indicatorColor.FillTint, useOriginalForFill: true);
                break;
            default:
                DrawCornerTriangle(image, size, useInvert: false);
                break;
        }
    }

    private static void DrawCornerTriangle(
        Image<Rgba32> image,
        int size,
        bool useInvert,
        Rgba32? fillTint = null,
        bool useOriginalForFill = false)
    {
        var blurRadius = Math.Max(2, size / 5);
        var shadowReach = Math.Max(3, (int)Math.Round(size * (FeishinShadowBlurPx + FeishinShadowSpreadPx) / (float)FeishinVisibleLegPx));
        var tint = fillTint ?? new Rgba32(255, 255, 255, (byte)(255 * 0.22f));
        var width = image.Width;
        var yMax = Math.Min(image.Height, size + shadowReach);
        var xMin = Math.Max(0, width - size - shadowReach);

        if (useOriginalForFill)
        {
            image.ProcessPixelRows(accessor =>
            {
                ProcessTopRightCorner(accessor, accessor, width, size, shadowReach, tint, useInvert, useFrostFill: false, yMax, xMin);
            });
            return;
        }

        using var frostSource = image.Clone(c => c.BoxBlur(blurRadius));
        image.ProcessPixelRows(frostSource, (target, frost) =>
        {
            ProcessTopRightCorner(target, frost, width, size, shadowReach, tint, useInvert, useFrostFill: true, yMax, xMin);
        });
    }

    private static void ProcessTopRightCorner(
        PixelAccessor<Rgba32> target,
        PixelAccessor<Rgba32> frost,
        int width,
        int size,
        int shadowReach,
        Rgba32 tint,
        bool useInvert,
        bool useFrostFill,
        int yMax,
        int xMin)
    {
        for (var y = 0; y < yMax; y++)
        {
            var targetRow = target.GetRowSpan(y);
            var frostRow = frost.GetRowSpan(y);
            var foldStartX = width - size + y;

            for (var x = xMin; x < width; x++)
            {
                var insideTriangle = y < size && x >= foldStartX;
                var shadowWeight = GetFoldShadowWeight(width, x, y, size, shadowReach);
                if (!insideTriangle && shadowWeight <= 0f)
                {
                    continue;
                }

                var basePixel = targetRow[x];
                var pixel = basePixel;

                if (insideTriangle)
                {
                    var fillBase = useFrostFill ? frostRow[x] : basePixel;
                    pixel = AlphaBlend(fillBase, tint);
                    if (useInvert)
                    {
                        pixel = InvertRgb(pixel);
                    }
                }

                if (shadowWeight > 0f)
                {
                    var shadowAlpha = (byte)Math.Clamp(255 * 0.8f * shadowWeight, 0, 204);
                    pixel = AlphaBlend(pixel, new Rgba32(0, 0, 0, shadowAlpha));
                }

                targetRow[x] = pixel;
            }
        }
    }

    private static float GetFoldShadowWeight(int width, int x, int y, int size, int shadowReach)
    {
        var outside = (width - x) - (size - y);
        if (outside <= 0f || outside > shadowReach)
        {
            return 0f;
        }

        var t = outside / (float)shadowReach;
        return (1f - t) * (1f - t);
    }

    private static Rgba32 InvertRgb(Rgba32 pixel) =>
        new((byte)(255 - pixel.R), (byte)(255 - pixel.G), (byte)(255 - pixel.B), pixel.A);

    private static Rgba32 AlphaBlend(Rgba32 background, Rgba32 foreground)
    {
        var alpha = foreground.A / 255f;
        var inv = 1f - alpha;
        return new Rgba32(
            (byte)(foreground.R * alpha + background.R * inv),
            (byte)(foreground.G * alpha + background.G * inv),
            (byte)(foreground.B * alpha + background.B * inv),
            background.A);
    }

    private static IImageEncoder GetEncoder(string contentType, IImageFormat sourceFormat)
    {
        if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ||
            sourceFormat.DefaultMimeType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return new PngEncoder();
        }

        return new JpegEncoder { Quality = 86 };
    }

    private static string GetOutputContentType(string contentType, IImageFormat sourceFormat)
    {
        if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ||
            sourceFormat.DefaultMimeType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        return "image/jpeg";
    }
}
