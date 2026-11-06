using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Qobuz;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Maps the configured SquidWTF quality to the codes each backend expects, and to the
/// format a track will be delivered in.
/// </summary>
public static class SquidWTFQuality
{
    /// <summary>
    /// Qobuz quality code: 27 = FLAC 24-bit/192kHz, 7 = FLAC 24-bit/96kHz,
    /// 6 = FLAC 16-bit/44kHz, 5 = MP3 320kbps. Defaults to the highest.
    /// </summary>
    public static string QobuzQuality(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return "27";
        }

        return quality.ToUpperInvariant() switch
        {
            "FLAC_24_192" or "FLAC_24" or "27" => "27",
            "FLAC_24_96" or "7" => "7",
            "FLAC_16" or "FLAC" or "6" => "6",
            "MP3_320" or "MP3" or "5" => "5",
            _ => "27"
        };
    }

    /// <summary>
    /// Tidal quality code, defaulting to the highest.
    /// </summary>
    public static string TidalQuality(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return "HI_RES_LOSSLESS";
        }

        return quality.ToUpperInvariant() switch
        {
            "HI_RES_LOSSLESS" or "HI_RES" or "FLAC_24" => "HI_RES_LOSSLESS",
            "LOSSLESS" or "FLAC" or "FLAC_16" => "LOSSLESS",
            "HIGH" or "AAC_320" or "AAC_HIGH" => "HIGH",
            "LOW" or "AAC_96" or "AAC_LOW" => "LOW",
            _ => "HI_RES_LOSSLESS"
        };
    }

    /// <summary>
    /// What the Qobuz backend delivers for a track of the given resolution.
    /// </summary>
    public static AudioFormat ExpectedQobuzFormat(string? quality, int? trackBitDepth, double? trackSampleRateKhz)
        => Qobuz.QobuzQuality.ExpectedFormat(int.Parse(QobuzQuality(quality)), trackBitDepth, trackSampleRateKhz);

    /// <summary>
    /// What the Tidal backend delivers for a track with the given audio quality and tags.
    /// </summary>
    public static AudioFormat ExpectedTidalFormat(string? quality, string? trackAudioQuality, IReadOnlyCollection<string>? tags)
        => Tidal.TidalQuality.ExpectedFormat(TidalQuality(quality), trackAudioQuality, tags);
}
