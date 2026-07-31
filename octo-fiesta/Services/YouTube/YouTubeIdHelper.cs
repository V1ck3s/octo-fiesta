using System.Text;

namespace octo_fiesta.Services.YouTube;

internal static class YouTubeIdHelper
{
    private const string ArtistPrefix = "ext-youtube-artist-";
    private const string AlbumPrefix = "ext-youtube-album-";
    private const string VideoAlbumPrefix = "video-";

    public static string BuildArtistExternalId(string? channelId, string? artistName)
    {
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            return channelId.Trim();
        }

        var slug = Slugify(artistName);
        return string.IsNullOrWhiteSpace(slug) ? "unknown-artist" : $"name-{slug}";
    }

    public static string BuildArtistId(string artistExternalId) => $"{ArtistPrefix}{artistExternalId}";

    public static string BuildAlbumExternalId(string trackExternalId) => $"{VideoAlbumPrefix}{trackExternalId}";

    public static string BuildAlbumId(string albumExternalId) => $"{AlbumPrefix}{albumExternalId}";

    public static string? TryExtractTrackIdFromAlbumExternalId(string albumExternalId)
    {
        if (albumExternalId.StartsWith(VideoAlbumPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return albumExternalId[VideoAlbumPrefix.Length..];
        }

        return null;
    }

    private static string Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        bool lastWasDash = false;
        foreach (var c in input.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        return sb.ToString().Trim('-');
    }
}
