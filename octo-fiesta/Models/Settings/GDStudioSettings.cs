namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the GD Studio music API (https://music-api.gdstudio.xyz)
/// </summary>
public class GDStudioSettings
{
    /// <summary>
    /// Upstream music source(s), comma separated, e.g. "netease,joox". Not validated here:
    /// any value the API accepts works. Each source is queried on its own and results are merged.
    /// Default: netease
    /// </summary>
    public string Source { get; set; } = "netease";

    /// <summary>Per-source timeout for search/metadata calls. Default: 5</summary>
    public int TimeoutSeconds { get; set; } = 5;

    public string[] Sources => Source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct().ToArray();

    // Track ids are only unique per source, so external ids are "<source>~<id>".
    public string TrackId(string source, string id) => $"{source}~{id}";

    /// <summary>Split an external track id; ids without a source prefix belong to the first source.</summary>
    public (string Source, string Id) SplitTrackId(string externalId)
    {
        var i = externalId.IndexOf('~');
        return i > 0 ? (externalId[..i], externalId[(i + 1)..]) : (Sources.FirstOrDefault() ?? "netease", externalId);
    }

    /// <summary>
    /// Optional proxy used for every call to the API and the download CDNs.
    /// Accepts http://, https:// and socks5:// URLs, e.g. socks5://127.0.0.1:1080
    /// </summary>
    public string? Proxy { get; set; }

    /// <summary>
    /// Full URL of the API endpoint. Default: https://music-api.gdstudio.xyz/api.php
    /// </summary>
    public string Api { get; set; } = "https://music-api.gdstudio.xyz/api.php";

    /// <summary>
    /// Audio quality (br): 128, 192, 320, 740 (16-bit lossless) or 999 (24-bit lossless).
    /// If it is unsupported or returns nothing, the next lower option is tried once.
    /// Default: 999
    /// </summary>
    public int Br { get; set; } = 999;

    public static readonly int[] ValidBr = [128, 192, 320, 740, 999];

    public string Url(string query) => $"{Api}?{query}";
}
