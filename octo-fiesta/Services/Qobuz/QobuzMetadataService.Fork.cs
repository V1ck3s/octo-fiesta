using System.Text.Json;

namespace octo_fiesta.Services.Qobuz;

public partial class QobuzMetadataService
{
    /// <summary>
    /// Fetches only the album cover instead of downloading the complete track list.
    /// </summary>
    public async Task<string?> GetAlbumCoverUrlAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "qobuz") return null;

        try
        {
            var appId = await _bundleService.GetAppIdAsync();
            var url = $"{BaseUrl}album/get?album_id={externalId}&app_id={appId}&limit=0&offset=0";

            var response = await GetWithAuthAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var albumElement = JsonDocument.Parse(json).RootElement;

            if (albumElement.TryGetProperty("error", out _)) return null;

            return GetLargeCoverArtUrl(albumElement) ?? GetCoverArtUrl(albumElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get album cover URL for {ExternalId}", externalId);
            return null;
        }
    }

    private static bool IsAlbumByArtist(JsonElement album, string artistId)
    {
        if (!album.TryGetProperty("artist", out var artist) ||
            artist.ValueKind != JsonValueKind.Object ||
            !artist.TryGetProperty("id", out var idEl))
        {
            return false;
        }

        var idStr = idEl.ValueKind switch
        {
            JsonValueKind.Number => idEl.GetInt64().ToString(),
            JsonValueKind.String => idEl.GetString() ?? string.Empty,
            _ => string.Empty,
        };

        return string.Equals(idStr, artistId, StringComparison.Ordinal);
    }
}
