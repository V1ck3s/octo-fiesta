using System.Globalization;
using SixLabors.ImageSharp.PixelFormats;

namespace octo_fiesta.Models.Settings;

public enum ExternalCoverIndicatorColorKind
{
    Frost,
    Invert,
    CustomFill
}

public readonly struct ExternalCoverIndicatorColor
{
    public ExternalCoverIndicatorColorKind Kind { get; }
    public Rgba32 FillTint { get; }

    public ExternalCoverIndicatorColor(ExternalCoverIndicatorColorKind kind, Rgba32 fillTint = default)
    {
        Kind = kind;
        FillTint = fillTint;
    }
}

public sealed class ExternalCoverSettings
{
    public const byte CustomFillAlpha = 190;

    public int IndicatorSize { get; set; } = 1;
    public int IndicatorSaturation { get; set; } = 1;
    public string IndicatorColor { get; set; } = "0";

    public int GetIndicatorSize() => ClampLevel(IndicatorSize);

    public int GetIndicatorSaturation() => ClampLevel(IndicatorSaturation);

    public ExternalCoverIndicatorColor ResolveIndicatorColor() =>
        TryResolveIndicatorColor(IndicatorColor, out var resolved) ? resolved : new ExternalCoverIndicatorColor(ExternalCoverIndicatorColorKind.Frost);

    public string GetCacheKeySegment() =>
        $"{GetIndicatorSize()}-{GetIndicatorSaturation()}-{GetColorCacheKeySegment(IndicatorColor)}";

    public static bool TryResolveIndicatorColor(string? raw, out ExternalCoverIndicatorColor color)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            color = default;
            return false;
        }

        if (value == "0")
        {
            color = new ExternalCoverIndicatorColor(ExternalCoverIndicatorColorKind.Frost);
            return true;
        }

        if (value == "1")
        {
            color = new ExternalCoverIndicatorColor(ExternalCoverIndicatorColorKind.Invert);
            return true;
        }

        if (TryParseHexFill(value, out var fillTint))
        {
            color = new ExternalCoverIndicatorColor(ExternalCoverIndicatorColorKind.CustomFill, fillTint);
            return true;
        }

        color = default;
        return false;
    }

    internal static string GetColorCacheKeySegment(string? raw)
    {
        if (TryResolveIndicatorColor(raw, out var resolved))
        {
            return resolved.Kind switch
            {
                ExternalCoverIndicatorColorKind.Frost => "0",
                ExternalCoverIndicatorColorKind.Invert => "1",
                ExternalCoverIndicatorColorKind.CustomFill =>
                    $"{resolved.FillTint.R:X2}{resolved.FillTint.G:X2}{resolved.FillTint.B:X2}",
                _ => "0",
            };
        }

        return "0";
    }

    internal static bool TryParseHexFill(string hex, out Rgba32 fillTint)
    {
        fillTint = default;
        var normalized = hex.Trim().TrimStart('#');
        if (normalized.Length == 3)
        {
            normalized = string.Concat(
                normalized[0], normalized[0],
                normalized[1], normalized[1],
                normalized[2], normalized[2]);
        }

        if (normalized.Length != 6 ||
            !uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        fillTint = new Rgba32(
            (byte)(rgb >> 16),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF),
            CustomFillAlpha);
        return true;
    }

    private static int ClampLevel(int value) => value switch
    {
        0 => 0,
        2 => 2,
        _ => 1
    };
}
