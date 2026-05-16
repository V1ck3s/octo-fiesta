using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using System.Text.Json;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using octo_fiesta.Models.Yandex;
using octo_fiesta.Services.Common;
using System.Text.Json.Serialization;

namespace octo_fiesta.Services.Yandex;

/// <summary>
/// Metadata service implementation using Yandex Music API.
/// </summary>
public class YandexMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<YandexMetadataService> _logger;
    private readonly bool _includeUnavailable;
    private const string BaseUrl = "https://api.music.yandex.net";
    private const string ProviderName = "yandex";
    private const string AlbumPrefix = "ext-yandex-album-";
    private const string ArtistPrefix = "ext-yandex-artist-";

    public YandexMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<YandexSettings>yandexSettings,
        ILogger<YandexMetadataService> logger
    )
    {
        _logger = logger;
        _includeUnavailable = yandexSettings.Value.IncludeUnavailable;
        _httpClient = httpClientFactory.CreateClient("Yandex");
    }
    
    #region Interface implementation
    /// <summary>
    /// Searches for songs on Yandex Music
    /// </summary>
    /// <param name="query">Search term</param>
    /// <param name="limit">Maximum number of results</param>
    /// <returns>List of found songs</returns>
    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        CombinedSearchResults combinedSearchResults = await SearchAsync(
            query,
            YandexSearchItemsType.TRACK,
            new YandexSearchLimits(track: limit)
        );
        return combinedSearchResults
            .Tracks
            .Items
            .Select(track => MapYandexTrackToSong(track))
            .ToList();
    }
    
    /// <summary>
    /// Searches for albums on Yandex Music
    /// </summary>
    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        CombinedSearchResults combinedSearchResults = await SearchAsync(
            query,
            YandexSearchItemsType.ALBUM,
            new YandexSearchLimits(album: limit)
        );
        return await GetAlbumsAsync(combinedSearchResults.Albums.Items);
    }
    

    /// <summary>
    /// Searches for artists on Yandex Music
    /// </summary>
    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        CombinedSearchResults combinedSearchResults = await SearchAsync(
            query,
            YandexSearchItemsType.ARTIST,
            new YandexSearchLimits(artist: limit)
        );
        return combinedSearchResults
            .Artists
            .Items
            .Select(MapYandexArtistToArtist)
            .ToList();
    }
    
    /// <summary>
    /// Combined search (songs, albums, artists)
    /// </summary>
    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        CombinedSearchResults combinedSearchResults = await SearchAsync(
            query,
            YandexSearchItemsType.ALL,
            new YandexSearchLimits(track: songLimit, album: albumLimit, artist: artistLimit)
        );

        List<Song> songs = combinedSearchResults
            .Tracks
            .Items
            .Select(track => MapYandexTrackToSong(track))
            .ToList();
        
        List<Album> albums = await GetAlbumsAsync(combinedSearchResults.Albums.Items);

        List<Artist> artists = combinedSearchResults
            .Artists
            .Items
            .Select(MapYandexArtistToArtist)
            .ToList();

        return new SearchResult
        {
            Songs = songs,
            Albums = albums,
            Artists = artists
        };
    }
    
    /// <summary>
    /// Gets details of an external song
    /// </summary>
    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var response = await SendApiRequestAsync<List<YandexTrack>>($"/tracks/{externalId}");
        YandexTrack? yandexTrack = response?.FirstOrDefault();

        if (yandexTrack is null) return null;

        if (yandexTrack.Error is not null)
        {
            _logger.LogWarning(
                "Yandex API returned an error ({Error}) for track {TrackId}",
                yandexTrack.Error, externalId
            );
            return null;
        }

        return MapYandexTrackToSong(yandexTrack);
    }
    
    /// <summary>
    /// Gets details of an external album with its songs
    /// </summary>
    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var yandexAlbum = await SendApiRequestAsync<YandexAlbumWithTracks>($"/albums/{externalId}/with-tracks");
        if (yandexAlbum is null) return null;

        if (yandexAlbum.Error is not null)
        {
            _logger.LogWarning(
                "Yandex API returned an error ({Error}) for album {AlbumId}",
                yandexAlbum.Error, externalId
            );
            return null;
        }
        return MapYandexAlbumToAlbum(yandexAlbum);
    }
    
    /// <summary>
    /// Gets details of an external artist
    /// </summary>
    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var yandexArtistWrapper = await SendApiRequestAsync<YandexArtistWrapper>($"/artists/{externalId}");
        if (yandexArtistWrapper?.Artist == null) return null;

        YandexArtist yandexArtist = yandexArtistWrapper.Artist;
        if (yandexArtist.Error is not null)
        {
            _logger.LogWarning(
                "Yandex API returned an error ({Error}) for artist {ArtistId}",
                yandexArtist.Error, externalId
            );
            return null;
        }
        return MapYandexArtistToArtist(yandexArtist);
    }
    
    /// <summary>
    /// Gets an artist's albums
    /// </summary>
    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return [];

        var yandexArtistDirectAlbums = await SendApiRequestAsync<YandexArtistDirectAlbums>($"/artists/{externalId}/direct-albums");
        if (yandexArtistDirectAlbums == null) return [];


        List<YandexAlbumId>? yandexAlbumIds = yandexArtistDirectAlbums.Albums;
        if (yandexAlbumIds is null)
        {
            _logger.LogWarning(
                "Yandex API returned invalid response for Artist {ArtistId} Albums", externalId
            );
            return [];
        }

        return await GetAlbumsAsync(yandexAlbumIds);
    }
    
    /// <summary>
    /// Searches for playlists on Yandex Music
    /// </summary>
    /// <param name="query">Search term</param>
    /// <param name="limit">Maximum number of results</param>
    /// <returns>List of found playlists</returns>
    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        CombinedSearchResults combinedSearchResults = await SearchAsync(
            query,
            YandexSearchItemsType.PLAYLIST,
            new YandexSearchLimits(playlist: limit)
        );
        return combinedSearchResults
            .Playlists
            .Items
            .Select(MapYandexPlaylistToExternalPlaylist)
            .ToList();
    }
    
    /// <summary>
    /// Gets details of an external playlist (metadata only, not tracks)
    /// </summary>
    /// <param name="externalProvider">Provider name ("yandex")</param>
    /// <param name="externalId">Yandex Playlist ID</param>
    /// <returns>Playlist details or null if not found</returns>
    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;

        var yandexPlaylist = await SendApiRequestAsync<YandexPlaylist>($"/playlist/{externalId}");
        if (yandexPlaylist == null) return null;

        return MapYandexPlaylistToExternalPlaylist(yandexPlaylist);
    }
    
    /// <summary>
    /// Gets all tracks from Yandex Music playlist
    /// </summary>
    /// <param name="externalProvider">Provider name ("yandex")</param>
    /// <param name="externalId">Yandex Playlist ID</param>
    /// <returns>List of songs in the playlist</returns>
    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return [];

        var yandexTracklist = await SendApiRequestAsync<YandexPlaylistTracks>($"/playlist/{externalId}");
        if (yandexTracklist == null) return [];

        List<YandexPlaylistTrackWrapper> tracks = yandexTracklist.Tracks;
        return tracks
            .Where(track => IsTrackAvailable(track.Track))
            .OrderBy(track => track.Index)
            .Index()
            .Select(pair => {
                Song song = MapYandexTrackToSong(pair.Item.Track);
                song.Track = pair.Index + 1;
                song.Album = yandexTracklist.Title ?? "Unknown Playlist";
                song.AlbumId = PlaylistIdHelper.CreatePlaylistId(ProviderName, externalId);
                return song;
            })
            .ToList();
    }

    #endregion

    #region Cross-API mappings

    /// <summary>
    /// Maps Yandex Track to Song domain model.
    /// </summary>
    /// <param name="yandexTrack">Yandex API Track payload.</param>
    /// <param name="linkedAlbum">If a track as acquired from an album, that album's details
    /// will be used to populate album-related Song details.
    /// </param>
    /// <returns>Song Domain Model</returns>
    private static Song MapYandexTrackToSong(YandexTrack yandexTrack, YandexAlbumWithTracks? linkedAlbum = null)
    {   
        YandexArtistShort? yandexArtist = yandexTrack.Artists?.FirstOrDefault();
        string? externalArtistId = yandexArtist?.Id.ToString();

        YandexTrackAlbum? yandexAlbum = null;
        if (linkedAlbum is not null && linkedAlbum.Error is null)
        {
            yandexAlbum = yandexTrack.Albums?.Find(a => a.Id == linkedAlbum.Id);
        }
        yandexAlbum ??= yandexTrack.Albums?.FirstOrDefault();

        string? externalAlbumId = yandexAlbum?.Id.ToString();

        string externalTrackId = string.IsNullOrEmpty(externalAlbumId)
                             ? yandexTrack.Id.ToString()
                             : $"{yandexTrack.Id}:{externalAlbumId}";


        string? coverUri = yandexTrack.CoverUri ?? yandexTrack.OgImage;

        int explicitWarning;
        bool explicitInDisclaimers = yandexTrack.Disclaimers?.Contains("explicit") ?? false;
        if (yandexTrack.ContentWarning == "explicit" 
         || explicitInDisclaimers)
        {
            explicitWarning = 1;
        }
        else if (yandexTrack.ContentWarning == "clean") explicitWarning = 3;
        else explicitWarning = 0;

        string title = yandexTrack.Title ?? string.Empty;
        if (!String.IsNullOrEmpty(yandexTrack.Version))
        {
            title += $" ({yandexTrack.Version})";
        }

        string albumTitle = yandexAlbum?.Title ?? string.Empty;
        if (!String.IsNullOrEmpty(yandexAlbum?.Version))
        {
            albumTitle += $" ({yandexAlbum.Version})";
        }

        return new Song
        {
            Title = title,
            ReleaseType = yandexAlbum?.Type,
            Artist = yandexArtist?.Name ?? string.Empty,
            ArtistId = string.IsNullOrEmpty(externalArtistId) ? null : ArtistPrefix + externalArtistId,
            Album = albumTitle,
            AlbumId = string.IsNullOrEmpty(externalAlbumId) ? null : AlbumPrefix + externalAlbumId,
            Duration = yandexTrack.DurationMs / 1000,
            Track = yandexAlbum?.TrackPosition?.Index,
            DiscNumber = yandexAlbum?.TrackPosition?.Volume,
            TotalTracks = yandexAlbum?.TrackCount,
            Year = yandexAlbum?.Year,
            Genre = null,
            CoverArtUrl = MakeCoverUri(coverUri, 300),
            CoverArtUrlLarge = MakeCoverUri(coverUri, 1000),
            ReleaseDate = yandexAlbum?.ReleaseDate?[..10],
            AlbumArtist = yandexAlbum?.Artists.FirstOrDefault()?.Name,
            Composer = null,
            Label = yandexAlbum?.Labels?.FirstOrDefault()?.Name,
            Artists = yandexTrack.Artists?.Select(a => a.Name)?.ToList() ?? [],
            Contributors = [],
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = externalTrackId,
            LocalPath = null,
            ExplicitContentLyrics = explicitWarning
        };
    }

    /// <summary>
    /// Maps Yandex Artist to Artist domain model.
    /// </summary>
    /// <param name="yandexArtist">Yandex Artist API payload.</param>
    /// <returns>Artist domain model.</returns>
    private static Artist MapYandexArtistToArtist(YandexArtist yandexArtist)
    {
        string externalId = yandexArtist.Id.ToString();
        string? coverUri = yandexArtist.Cover?.Uri ?? yandexArtist.OgImage;

        return new Artist
        {
            Id = ArtistPrefix + externalId,
            Name = yandexArtist.Name ?? string.Empty,
            ImageUrl = MakeCoverUri(coverUri, 600),
            AlbumCount = yandexArtist.Counts?.DirectAlbums,
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = externalId
        };
    }

    /// <summary>
    /// Maps Yandex Album With Tracks model to Album domain model.
    /// </summary>
    /// <param name="yandexAlbum">Album with Tracks Yandex API response</param>
    /// <returns>Album domain model</returns>
    private Album MapYandexAlbumToAlbum(YandexAlbumWithTracks yandexAlbum)
    {
        string externalId = yandexAlbum.Id.ToString();

        YandexArtistShort? yandexArtist = yandexAlbum.Artists?.FirstOrDefault();
        string? externalArtistId = yandexArtist?.Id.ToString();

        string? coverUri = yandexAlbum.CoverUri ?? yandexAlbum.Cover?.Uri ?? yandexAlbum.OgImage;

        string title = yandexAlbum.Title ?? string.Empty;
        if (!String.IsNullOrEmpty(yandexAlbum.Version))
        {
            title += $" ({yandexAlbum.Version})";
        }

        return new Album
        {
            Id = AlbumPrefix + externalId,
            Title = title,
            ReleaseType = yandexAlbum.Type,
            Artist = yandexArtist?.Name ?? string.Empty,
            ArtistId = string.IsNullOrEmpty(externalArtistId) ? null : ArtistPrefix + externalArtistId,
            Year = yandexAlbum.Year,
            SongCount = yandexAlbum.TrackCount,
            CoverArtUrl = MakeCoverUri(coverUri, 300),
            CoverArtUrlLarge = MakeCoverUri(coverUri, 1000),
            Genre = yandexAlbum.Genre,
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = externalId,
            Songs = yandexAlbum.Volumes?.SelectMany(trackList => 
                trackList.Where(IsTrackAvailable).Select(track =>
                    MapYandexTrackToSong(track, yandexAlbum)
                )
            ).ToList() ?? [],
        };
    }

    /// <summary>
    /// Maps Yandex Playlist model to domain ExternalPlaylist model.
    /// </summary>
    /// <param name="yandexPlaylist">Yandex Playlist payload.</param>
    /// <returns>ExternalPlaylist model</returns>
    private static ExternalPlaylist MapYandexPlaylistToExternalPlaylist(YandexPlaylist yandexPlaylist)
    {
        string? curatorName = null;
        var owner = yandexPlaylist.Owner;
        if (owner is not null)
        {
            curatorName = owner.Name ?? owner.Login;
        }

        string? coverUri = yandexPlaylist.Cover?.Uri 
                        ?? yandexPlaylist.Cover?.ItemsUri?.FirstOrDefault()
                        ?? yandexPlaylist.OgImage;

        DateTime? creationDate = null;
        try
        {
            if (yandexPlaylist.CreatedAt is not null)
            {
                creationDate = DateTime.Parse(yandexPlaylist.CreatedAt);
            }
        }
        catch (System.FormatException) {}

        return new ExternalPlaylist
        {
            Id = PlaylistIdHelper.CreatePlaylistId(ProviderName, yandexPlaylist.Id),
            Name = yandexPlaylist.Title ?? "Unknown Playlist",
            Description = yandexPlaylist.Description,
            CuratorName = curatorName,
            Provider = ProviderName,
            ExternalId = yandexPlaylist.Id,
            TrackCount = yandexPlaylist.TrackCount,
            Duration = yandexPlaylist.DurationMs / 1000,
            CoverUrl = MakeCoverUri(coverUri, 600),
            CreatedDate = creationDate
        };
    }

    #endregion

    #region Utility methods

    /// <summary>
    /// Common utility function to make API requests. 
    /// Calls an API and returns parsed results.
    /// </summary>
    /// <typeparam name="T">Type of response to parse.</typeparam>
    /// <param name="url">URL to call without Base Domain.</param>
    /// <returns>Parsed results of API call.</returns>
    private async Task<T?> SendApiRequestAsync<T>(string url) where T: class
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Yandex API returned status code {StatausCode} for {Url}", response.StatusCode, url);
                return null;
            }
            
            string responseString = await response.Content.ReadAsStringAsync();
            var jsonSerializerOptions = new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            };

            YandexResponse<T>? parsedResponse = JsonSerializer.Deserialize<YandexResponse<T>>(responseString, jsonSerializerOptions);
            if (parsedResponse is null)
            {
                _logger.LogWarning("Unable to parse Yandex API response for {Url}", url);
                return null;
            }

            if (parsedResponse.Error is not null)
            {
                _logger.LogWarning(
                    "Yandex API returned an error ({ErrorName} {ErrorMessage}) for {Url}",
                    parsedResponse.Error.Name, parsedResponse.Error.Message, url
                );
                return null;
            }
            
            return parsedResponse.Result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Yandex request to {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Generalized search function. 
    /// Depending on argumets passed searches for Tracks, Albums, Artists or all of them.
    /// Or Playlists.
    /// </summary>
    /// <param name="query">User input.</param>
    /// <param name="itemsType">Type of items (Tracks, Albums, Artists, All, Playlists)</param>
    /// <param name="limits">Response limits for Tracks, Albums, Artists and Playlists results.</param>
    /// <returns>Combined Search Results</returns>
    private async Task<CombinedSearchResults> SearchAsync(
        string query, 
        YandexSearchItemsType itemsType,
        YandexSearchLimits limits
    )
    {
        CombinedSearchResults combinedSearchResults = new(limits);

        if (string.IsNullOrWhiteSpace(query)) return combinedSearchResults;

        int pageNumber = 0;
        bool keepPaging = true;
        while (keepPaging)
        {
            var searchResults = await SendApiRequestAsync<YandexSearchResults>(
                $"/search?text={Uri.EscapeDataString(query)}&type={itemsType.Value}&nocorrect=false&playlist-in-best=false&page={pageNumber}"
            );
            if (searchResults is null) break;

            YandexSearchBestResult? bestResult = searchResults.Best;
            if (itemsType == YandexSearchItemsType.ALL && pageNumber == 0 && bestResult is not null)
            {
                if (bestResult.Type == "track" && bestResult.Track is not null && IsTrackAvailable(bestResult.Track))
                {
                    combinedSearchResults.Tracks.AddResultsIfNeeded([bestResult.Track]);
                }
                if (bestResult.Type == "album" && bestResult.Album is not null)
                {
                    combinedSearchResults.Albums.AddResultsIfNeeded([bestResult.Album]);
                }
                if (bestResult.Type == "artist" && bestResult.Artist is not null)
                {
                    combinedSearchResults.Artists.AddResultsIfNeeded([bestResult.Artist]);
                }
            }

            List<YandexTrack> yandexTracks = searchResults.Tracks?.Results?.Where(IsTrackAvailable).ToList() ?? [];
            combinedSearchResults.Tracks.AddResultsIfNeeded(yandexTracks);

            List<YandexAlbumId> yandexAlbums = searchResults.Albums?.Results ?? [];
            combinedSearchResults.Albums.AddResultsIfNeeded(yandexAlbums);

            List<YandexArtist> yandexArtists = searchResults.Artists?.Results ?? [];
            combinedSearchResults.Artists.AddResultsIfNeeded(yandexArtists);      

            List<YandexPlaylist> yandexPlaylists = searchResults.Playlists?.Results ?? [];
            combinedSearchResults.Playlists.AddResultsIfNeeded(yandexPlaylists);

            // if current page isn't empty and limits aren't reached
            // then fetch the next page for more results
            keepPaging = yandexTracks.Count != 0 && !combinedSearchResults.Tracks.LimitReached
                      || yandexAlbums.Count != 0 && !combinedSearchResults.Albums.LimitReached
                      || yandexArtists.Count != 0 && !combinedSearchResults.Artists.LimitReached
                      || yandexPlaylists.Count != 0 && !combinedSearchResults.Playlists.LimitReached;
            pageNumber++;
        }
        return combinedSearchResults;        
    }

    /// <summary>
    /// Asynchronously fetches multiple Yandex albums with tracks
    /// and converts them to list of Album domain model.
    /// </summary>
    /// <param name="yandexAlbums">List of Yandex Album IDs wrapped in YandexAlbumId model.</param>
    /// <returns>List of domain Albums.</returns>
    private async Task<List<Album>> GetAlbumsAsync(List<YandexAlbumId> yandexAlbums)
    {
        IEnumerable<Task<Album?>> tasks = yandexAlbums.Select(album =>
            GetAlbumAsync(ProviderName, album.Id.ToString())
        );
        var albums = await Task.WhenAll(tasks);

        return albums.OfType<Album>().ToList();
    }

    /// <summary>
    /// Utility method to convert yandex Cover uri string to an actual uri
    /// </summary>
    /// <param name="originalUri">Uri string obtained from Yandex API.</param>
    /// <param name="size">Size in pixels of a side of square cover image.</param>
    /// <returns>Actual uri with specified size.</returns>
    private static string? MakeCoverUri(string? originalUri, int size)
    {
        if (string.IsNullOrWhiteSpace(originalUri)) return null;

        return $"https://{originalUri}".Replace("%%", $"{size}x{size}");
    }

    private bool IsTrackAvailable(YandexTrack track)
    {
        return track.Available != false || _includeUnavailable;
    }

    #endregion
}
