namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the GD Studio music API (https://music-api.gdstudio.xyz)
/// </summary>
public class GDStudioSettings
{
    /// <summary>
    /// Upstream music source. Any of the "stable" sources listed on the API page
    /// (currently netease, joox, bilibili). Default: netease
    /// </summary>
    public string Source { get; set; } = "netease";

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
