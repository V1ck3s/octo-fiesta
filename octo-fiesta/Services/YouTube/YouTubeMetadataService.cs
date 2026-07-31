using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.YouTube;

public class YouTubeMetadataService : IMusicMetadataService
{
    private const string ProviderName = "youtube";
    private const string AlbumPrefix = "ext-youtube-album-";
    private readonly ILogger<YouTubeMetadataService> _logger;
    private readonly YouTubeSettings _settings;
    private readonly Dictionary<string, string> _artistNameByExternalId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _artistLock = new();

    public YouTubeMetadataService(
        IOptions<YouTubeSettings> youtubeSettings,
        ILogger<YouTubeMetadataService> logger)
    {
        _settings = youtubeSettings.Value;
        _logger = logger;
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3)
	{
            return [];
        }

        var effectiveLimit = Math.Clamp(limit, 1, Math.Max(1, _settings.MaxResults));
        _logger.LogInformation("YouTube search started: query='{Query}', limit={Limit}", query, effectiveLimit);

        var args = new List<string>
        {
            "--dump-single-json",
            "--skip-download",
            "--flat-playlist",
            "--no-warnings",
            $"ytsearch{effectiveLimit}:{query}"
        };

        AddCookiesArgument(args);

        var result = await YtDlpProcessRunner.ExecuteAsync(_settings.YtDlpPath, args, CancellationToken.None);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("YouTube search failed for query '{Query}'. stderr: {Error}", query, result.StandardError);
            return [];
        }

        using var doc = JsonDocument.Parse(result.StandardOutput);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            _logger.LogInformation("YouTube search returned no entries for query '{Query}'", query);
            return [];
        }

        var songs = new List<Song>();
        foreach (var entry in entries.EnumerateArray())
        {
            var mapped = MapEntryToSong(entry);
            if (mapped != null)
            {
                songs.Add(mapped);
            }
        }

        _logger.LogInformation("YouTube search completed: query='{Query}', hits={Hits}", query, songs.Count);
        return songs;
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        var songs = await SearchSongsAsync(query, limit);
        return songs
            .Where(s => !string.IsNullOrWhiteSpace(s.AlbumId) && !string.IsNullOrWhiteSpace(s.ExternalId))
            .Select(s => new Album
            {
                Id = s.AlbumId!,
                Title = s.Album,
                Artist = s.Artist,
                ArtistId = s.ArtistId,
                SongCount = 1,
                CoverArtUrl = s.CoverArtUrl,
                IsLocal = false,
                ExternalProvider = ProviderName,
                ExternalId = ExtractAlbumExternalId(s.AlbumId!)
            })
            .ToList();
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        var songs = await SearchSongsAsync(query, limit);
        return songs
            .Where(s => !string.IsNullOrWhiteSpace(s.ArtistId))
            .GroupBy(s => s.ArtistId!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new Artist
                {
                    Id = first.ArtistId!,
                    Name = first.Artist,
                    ImageUrl = first.CoverArtUrl,
                    AlbumCount = null,
                    IsLocal = false,
                    ExternalProvider = ProviderName,
                    ExternalId = ExtractArtistExternalId(first.ArtistId!)
                };
            })
            .ToList();
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var songs = await SearchSongsAsync(query, songLimit);
        var albums = songs
            .Take(albumLimit)
            .Where(s => !string.IsNullOrWhiteSpace(s.AlbumId))
            .Select(s => new Album
            {
                Id = s.AlbumId!,
                Title = s.Album,
                Artist = s.Artist,
                ArtistId = s.ArtistId,
                SongCount = 1,
                CoverArtUrl = s.CoverArtUrl,
                IsLocal = false,
                ExternalProvider = ProviderName,
                ExternalId = ExtractAlbumExternalId(s.AlbumId!)
            })
            .ToList();

        var artists = songs
            .Where(s => !string.IsNullOrWhiteSpace(s.ArtistId))
            .GroupBy(s => s.ArtistId!, StringComparer.OrdinalIgnoreCase)
            .Take(artistLimit)
            .Select(group =>
            {
                var first = group.First();
                return new Artist
                {
                    Id = first.ArtistId!,
                    Name = first.Artist,
                    ImageUrl = first.CoverArtUrl,
                    IsLocal = false,
                    ExternalProvider = ProviderName,
                    ExternalId = ExtractArtistExternalId(first.ArtistId!)
                };
            })
            .ToList();

        return new SearchResult
        {
            Songs = songs,
            Albums = albums,
            Artists = artists
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (!externalProvider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        var args = new List<string>
        {
            "--dump-single-json",
            "--skip-download",
            "--no-warnings",
            "--no-playlist",
            $"https://www.youtube.com/watch?v={externalId}"
        };

        AddCookiesArgument(args);

        var result = await YtDlpProcessRunner.ExecuteAsync(_settings.YtDlpPath, args, CancellationToken.None);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("YouTube metadata resolve failed for {ExternalId}. stderr: {Error}", externalId, result.StandardError);
            return null;
        }

        using var doc = JsonDocument.Parse(result.StandardOutput);
        return MapEntryToSong(doc.RootElement, externalId);
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (!externalProvider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        var trackExternalId = YouTubeIdHelper.TryExtractTrackIdFromAlbumExternalId(externalId);
        if (string.IsNullOrWhiteSpace(trackExternalId))
        {
            return null;
        }

        var song = await GetSongAsync(ProviderName, trackExternalId);
        if (song == null || string.IsNullOrWhiteSpace(song.AlbumId))
        {
            return null;
        }

        return new Album
        {
            Id = song.AlbumId,
            Title = song.Album,
            Artist = song.Artist,
            ArtistId = song.ArtistId,
            SongCount = 1,
            CoverArtUrl = song.CoverArtUrl,
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = externalId,
            Songs = [song]
        };
    }

    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (!externalProvider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(externalId))
        {
            return Task.FromResult<Artist?>(null);
        }

        string? name = null;
        lock (_artistLock)
        {
            _artistNameByExternalId.TryGetValue(externalId, out name);
        }

        return Task.FromResult<Artist?>(new Artist
        {
            Id = YouTubeIdHelper.BuildArtistId(externalId),
            Name = name ?? externalId,
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = externalId
        });
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (!externalProvider.Equals(ProviderName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(externalId))
        {
            return [];
        }

        string? artistName;
        lock (_artistLock)
        {
            _artistNameByExternalId.TryGetValue(externalId, out artistName);
        }

        var query = string.IsNullOrWhiteSpace(artistName) ? externalId : artistName;
        var songs = await SearchSongsAsync(query, _settings.MaxResults);
        return songs
            .Where(s => !string.IsNullOrWhiteSpace(s.ArtistId) && s.ArtistId.Equals(YouTubeIdHelper.BuildArtistId(externalId), StringComparison.OrdinalIgnoreCase))
            .Where(s => !string.IsNullOrWhiteSpace(s.AlbumId))
            .Select(s => new Album
            {
                Id = s.AlbumId!,
                Title = s.Album,
                Artist = s.Artist,
                ArtistId = s.ArtistId,
                SongCount = 1,
                CoverArtUrl = s.CoverArtUrl,
                IsLocal = false,
                ExternalProvider = ProviderName,
                ExternalId = ExtractAlbumExternalId(s.AlbumId!)
            })
            .ToList();
    }

    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());

    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) => Task.FromResult<ExternalPlaylist?>(null);

    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) => Task.FromResult(new List<Song>());

    private Song? MapEntryToSong(JsonElement entry, string? fallbackExternalId = null)
    {
        var externalId = TryGetString(entry, "id") ?? fallbackExternalId;
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        var title = TryGetString(entry, "track")
                    ?? TryGetString(entry, "title")
                    ?? "Unknown title";
        var artistName = TryGetString(entry, "artist")
                         ?? TryGetString(entry, "channel")
                         ?? TryGetString(entry, "uploader")
                         ?? "Unknown artist";
        var channelId = TryGetString(entry, "channel_id")
                        ?? TryGetString(entry, "uploader_id");
        var albumName = TryGetString(entry, "album") ?? "YouTube";
        var artistExternalId = YouTubeIdHelper.BuildArtistExternalId(channelId, artistName);
        var artistId = YouTubeIdHelper.BuildArtistId(artistExternalId);
        var albumExternalId = YouTubeIdHelper.BuildAlbumExternalId(externalId);
        var albumId = YouTubeIdHelper.BuildAlbumId(albumExternalId);
        var thumbnail = TryGetString(entry, "thumbnail") ?? TryGetLastThumbnail(entry);
        var duration = TryGetInt(entry, "duration");
        var releaseDate = TryNormalizeUploadDate(TryGetString(entry, "upload_date"));

        lock (_artistLock)
        {
            _artistNameByExternalId[artistExternalId] = artistName;
        }

        return new Song
        {
            Title = title,
            Artist = artistName,
            Artists =
            [
                new Artist
                {
                    Id = artistId,
                    Name = artistName,
                    IsLocal = false,
                    ExternalProvider = ProviderName,
                    ExternalId = artistExternalId
                }
            ],
            ArtistId = artistId,
            Album = albumName,
            AlbumId = albumId,
            Duration = duration,
            CoverArtUrl = thumbnail,
            CoverArtUrlLarge = thumbnail,
            ReleaseDate = releaseDate,
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = externalId
        };
    }

    private void AddCookiesArgument(List<string> args)
    {
        if (!string.IsNullOrWhiteSpace(_settings.CookiesPath))
        {
            args.Add("--cookies");
            args.Add(_settings.CookiesPath);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
        {
            return i;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static string? TryGetLastThumbnail(JsonElement element)
    {
        if (!element.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var thumb in thumbnails.EnumerateArray().Reverse())
        {
            if (thumb.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }

        return null;
    }

    private static string? TryNormalizeUploadDate(string? uploadDate)
    {
        if (string.IsNullOrWhiteSpace(uploadDate) || uploadDate!.Length != 8)
        {
            return null;
        }

        if (!int.TryParse(uploadDate.AsSpan(0, 4), out _))
        {
            return null;
        }

        return $"{uploadDate[..4]}-{uploadDate[4..6]}-{uploadDate[6..8]}";
    }

    private static string? ExtractAlbumExternalId(string albumId)
    {
        return albumId.StartsWith(AlbumPrefix, StringComparison.OrdinalIgnoreCase)
            ? albumId[AlbumPrefix.Length..]
            : null;
    }

    private static string ExtractArtistExternalId(string artistId)
    {
        const string artistPrefix = "ext-youtube-artist-";
        return artistId.StartsWith(artistPrefix, StringComparison.OrdinalIgnoreCase)
            ? artistId[artistPrefix.Length..]
            : artistId;
    }
}
