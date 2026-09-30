using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.Local;

public partial class LocalLibraryService
{
    public Task RegisterDownloadedSongAsync(Song song, string localPath, string? downloadedQuality = null)
        => RegisterDownloadedSongAsync(song, localPath, downloadedQuality, localSubsonicId: null);

    public async Task RegisterDownloadedSongAsync(
        Song song,
        string localPath,
        string? downloadedQuality,
        string? localSubsonicId)
    {
        if (song.ExternalProvider == null || song.ExternalId == null) return;

        var mappings = await LoadMappingsAsync();

        await _lock.WaitAsync();
        try
        {
            var key = $"{song.ExternalProvider}:{song.ExternalId}";
            mappings[key] = new LocalSongMapping
            {
                ExternalProvider = song.ExternalProvider,
                ExternalId = song.ExternalId,
                LocalPath = localPath,
                LocalSubsonicId = localSubsonicId,
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                DownloadedAt = DateTime.UtcNow,
                DownloadedQuality = downloadedQuality
            };

            await SaveMappingsAsync(mappings);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, LocalSongMapping>> GetMappingsSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var mappings = await LoadMappingsAsync();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            return new Dictionary<string, LocalSongMapping>(mappings);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<LocalSongMatch?> FindLocalSongByMetadataAsync(Song song)
    {
        var title = song.Title;
        var artist = song.Artist;
        var album = song.Album;

        if (string.IsNullOrWhiteSpace(title) || (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album)))
        {
            return null;
        }

        try
        {
            var authQuery = BuildAuthQuery(_subsonicUserCredentials);
            var queryText = string.Join(" ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var searchUrl = $"{_subsonicSettings.Url}/rest/search3?f=json&songCount=10&albumCount=0&artistCount=0&query={Uri.EscapeDataString(queryText)}{authQuery}";

            var response = await _httpClient.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("subsonic-response", out var subsonicResponse) ||
                !subsonicResponse.TryGetProperty("searchResult3", out var searchResult) ||
                !searchResult.TryGetProperty("song", out var songNode))
            {
                return null;
            }

            var titleKey = StringNormalizer.CreateSongTitleDedupeKey(title);
            var artistKey = StringNormalizer.CreateComparisonKey(artist);
            var albumKey = StringNormalizer.CreateComparisonKey(album);

            foreach (var songElement in EnumerateSongs(songNode))
            {
                var candidateId = songElement.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                if (string.IsNullOrEmpty(candidateId)) continue;

                var candidateTitleKey = StringNormalizer.CreateSongTitleDedupeKey(
                    songElement.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null);
                var candidateArtistKey = StringNormalizer.CreateComparisonKey(
                    songElement.TryGetProperty("artist", out var artistEl) ? artistEl.GetString() : null);
                var candidateAlbumKey = StringNormalizer.CreateComparisonKey(
                    songElement.TryGetProperty("album", out var albumEl) ? albumEl.GetString() : null);
                var candidatePath = songElement.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;

                var titleMatches = !string.IsNullOrEmpty(titleKey) && titleKey == candidateTitleKey;
                var artistMatches = !string.IsNullOrEmpty(artistKey) && artistKey == candidateArtistKey;
                var albumMatches = !string.IsNullOrEmpty(albumKey) && albumKey == candidateAlbumKey;

                if ((titleMatches && artistMatches) || (titleMatches && albumMatches))
                {
                    return new LocalSongMatch(candidateId, candidatePath);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FindLocalSongByMetadataAsync failed for song '{Title}' by '{Artist}'", title, artist);
            return null;
        }
    }
}

public record LocalSongMatch(string LocalSubsonicId, string? LocalPath);
