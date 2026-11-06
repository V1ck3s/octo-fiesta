using octo_fiesta.Models.Domain;

namespace octo_fiesta.Services.Qobuz;

/// <summary>
/// Qobuz format IDs, the configured quality they come from, and what a track will be delivered in.
/// </summary>
public static class QobuzQuality
{
    public const int Mp3320 = 5;
    public const int Flac16 = 6;      // CD quality (16-bit 44.1kHz)
    public const int Flac24Low = 7;   // 24-bit up to 96kHz
    public const int Flac24High = 27; // 24-bit up to 192kHz

    /// <summary>
    /// Format ID for a configured quality, the highest one when empty or unknown.
    /// </summary>
    public static int GetFormatId(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return Flac24High;
        }

        return quality.ToUpperInvariant() switch
        {
            "FLAC" => Flac24High,
            "FLAC_24_HIGH" or "24_192" or "27" => Flac24High,
            "FLAC_24_LOW" or "24_96" or "7" => Flac24Low,
            "FLAC_16" or "CD" or "6" => Flac16,
            "MP3_320" or "MP3" or "5" => Mp3320,
            _ => Flac24High
        };
    }

    /// <summary>
    /// The format Qobuz serves a track in: the track's own resolution, brought down to what the
    /// requested format allows (Qobuz downsamples rather than refuses). A track whose resolution
    /// is unknown is assumed to be CD quality, as most of the catalog is.
    /// </summary>
    public static AudioFormat ExpectedFormat(int formatId, int? trackBitDepth, double? trackSampleRateKhz)
    {
        if (formatId == Mp3320)
        {
            return AudioFormat.Mp3_320;
        }

        var bitDepth = trackBitDepth is > 0 ? trackBitDepth.Value : 16;
        var sampleRate = trackSampleRateKhz is > 0 ? trackSampleRateKhz.Value : 44.1;

        if (formatId == Flac16)
        {
            return AudioFormat.Flac(16, 44.1);
        }
        if (bitDepth <= 16)
        {
            return AudioFormat.Flac(16, sampleRate);
        }

        var maxSampleRate = formatId == Flac24Low ? 96 : 192;
        return AudioFormat.Flac(24, Math.Min(sampleRate, maxSampleRate));
    }
}
