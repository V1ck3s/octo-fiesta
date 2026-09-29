namespace octo_fiesta.Services.Common;

/// <summary>
/// Human-readable provider names for places that show the provider to users
/// (e.g. the "🎵 Provider Curator" artist line of provider playlists).
/// </summary>
public static class ProviderNames
{
    public static string Display(string provider) => provider.ToLowerInvariant() switch
    {
        "applemusic" => "Apple Music",
        _ when provider.Length > 0 => char.ToUpper(provider[0]) + provider[1..],
        _ => provider,
    };
}
