using octo_fiesta.Models;

namespace octo_fiesta.Services;

/// <summary>
/// Interface for external music metadata search service
/// (Deezer API, Spotify API, MusicBrainz, etc.)
/// </summary>
public interface IMusicMetadataService
{
    /// <summary>
    /// Searches for songs on external providers
    /// </summary>
    /// <param name="query">Search term</param>
    /// <param name="limit">Maximum number of results</param>
    /// <returns>List of found songs</returns>
    Task<List<Song>> SearchSongsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Searches for albums on external providers
    /// </summary>
    Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Searches for artists on external providers
    /// </summary>
    Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Combined search (songs, albums, artists)
    /// </summary>
    Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20);
    
    /// <summary>
    /// Gets details of an external song
    /// </summary>
    Task<Song?> GetSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets details of an external album with its songs
    /// </summary>
    Task<Album?> GetAlbumAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets details of an external artist
    /// </summary>
    Task<Artist?> GetArtistAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets an artist's albums
    /// </summary>
    Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId);
}
