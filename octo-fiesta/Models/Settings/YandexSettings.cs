namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the Yandex downloader and metadata service
/// </summary>
public class YandexSettings
{
    /// <summary>
    /// Yandex OAuth token (required)
    /// Obtained from browser #access_token fragment after authenticating on 
    /// https://oauth.yandex.ru/authorize?response_type=token&client_id=23cabbbdc6cd418abb4b39c32c41195d
    /// </summary>
    public string? OAuthToken { get; set; }
    
    /// <summary>
    /// Preferred audio quality
    /// If not specified or unavailable, the highest available quality will be used.
    /// Default: FLAC
    /// Available: FLAC, MP3_320, AAC_256, AAC_192, MP3_192, AAC_64
    /// </summary>
    public string? Quality { get; set; } = "FLAC";

    /// <summary>
    /// Include unavailable songs in albums and search results.
    /// Default: false
    /// </summary>
    public bool IncludeUnavailable { get; set; } = false;

    /// <summary>
    /// Language used by Yandex API. Some tracks, albums and playlists
    /// will be translated to this language.
    /// Default: ru
    /// Available: en/uz/uk/us/ru/kk/hy
    /// </summary>
    public string Language { get; set; } = "ru";
}
