namespace octo_fiesta.Services.Tidal;

/// <summary>
/// Maps between the configured quality name and the audioquality codes accepted by
/// Tidal's playbackinfopostpaywall endpoint, and describes the fallback ladder used
/// when the account is not entitled to the requested tier.
/// </summary>
public static class TidalQuality
{
    public const string HiResLossless = "HI_RES_LOSSLESS";
    public const string Lossless = "LOSSLESS";
    public const string High = "HIGH";
    public const string Low = "LOW";

    /// <summary>
    /// Legacy MQA tier, still accepted by the API and by older configurations.
    /// </summary>
    public const string HiRes = "HI_RES";

    /// <summary>
    /// Quality tiers from best to worst. Downloading walks down this ladder when a tier
    /// is refused, so an account without HiFi Plus still gets the best it can stream.
    /// </summary>
    private static readonly string[] Ladder = [HiResLossless, Lossless, High, Low];

    private static readonly HashSet<string> ValidValues = new(StringComparer.OrdinalIgnoreCase)
    {
        HiResLossless, HiRes, Lossless, High, Low
    };

    public static IReadOnlyList<string> ValidQualities => [HiResLossless, Lossless, High, Low];

    /// <summary>
    /// An empty quality is valid and means "highest available".
    /// </summary>
    public static bool IsValid(string? quality)
        => string.IsNullOrWhiteSpace(quality) || ValidValues.Contains(quality.Trim());

    /// <summary>
    /// Normalizes a configured quality to an audioquality code, defaulting to the highest tier.
    /// </summary>
    public static string Normalize(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return HiResLossless;
        }

        return quality.Trim().ToUpperInvariant() switch
        {
            HiResLossless or "FLAC_24" => HiResLossless,
            HiRes or "MQA" => HiRes,
            Lossless or "FLAC" or "FLAC_16" => Lossless,
            High or "AAC_320" => High,
            Low or "AAC_96" => Low,
            _ => HiResLossless
        };
    }

    /// <summary>
    /// Next tier down, or null when already at the lowest one.
    /// HI_RES is an alias of the legacy MQA tier and falls back to plain LOSSLESS.
    /// </summary>
    public static string? NextLower(string quality)
    {
        if (string.Equals(quality, HiRes, StringComparison.OrdinalIgnoreCase))
        {
            return Lossless;
        }

        var index = Array.FindIndex(Ladder, q => string.Equals(q, quality, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index < Ladder.Length - 1 ? Ladder[index + 1] : null;
    }

    /// <summary>
    /// File extension for the delivered stream. FLAC inside MP4, delivered by the DASH
    /// HI_RES_LOSSLESS path, keeps the .m4a container: the audio is lossless but the bytes
    /// are fragmented MP4, so naming it .flac would mislead players.
    /// </summary>
    public static string GetExtension(string? mimeType, string? codecs)
    {
        var isFlacInMp4 = codecs?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true
                          && mimeType?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true;
        if (isFlacInMp4)
        {
            return ".m4a";
        }

        if (string.IsNullOrEmpty(mimeType))
        {
            return codecs?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true ? ".flac" : ".m4a";
        }

        return mimeType.ToLowerInvariant() switch
        {
            var m when m.Contains("flac") => ".flac",
            var m when m.Contains("mp4") || m.Contains("m4a") || m.Contains("aac") => ".m4a",
            var m when m.Contains("mp3") || m.Contains("mpeg") => ".mp3",
            _ => ".m4a"
        };
    }

    /// <summary>
    /// Quality label stored with the downloaded file, used by the quality upgrade check
    /// and by the {quality} placeholder of the folder template.
    /// </summary>
    public static string GetDownloadedQuality(string requestedQuality, string? mimeType, string? codecs)
    {
        var isFlac = codecs?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true
                     || mimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true;

        if (isFlac)
        {
            return string.Equals(requestedQuality, HiResLossless, StringComparison.OrdinalIgnoreCase)
                ? "FLAC_24"
                : "FLAC_16";
        }

        if (codecs?.Contains("mqa", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(requestedQuality, HiRes, StringComparison.OrdinalIgnoreCase))
        {
            return "FLAC_24";
        }

        return string.Equals(requestedQuality, Low, StringComparison.OrdinalIgnoreCase) ? "AAC_96" : "AAC_320";
    }
}
