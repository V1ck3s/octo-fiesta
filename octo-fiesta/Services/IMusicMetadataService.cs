using octo_fiesta.Models;

namespace octo_fiesta.Services;

/// <summary>
/// Interface pour le service de recherche de métadonnées musicales externes
/// (Deezer API, Spotify API, MusicBrainz, etc.)
/// </summary>
public interface IMusicMetadataService
{
    /// <summary>
    /// Recherche des chansons sur les providers externes
    /// </summary>
    /// <param name="query">Terme de recherche</param>
    /// <param name="limit">Nombre maximum de résultats</param>
    /// <returns>Liste des chansons trouvées</returns>
    Task<List<Song>> SearchSongsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Recherche des albums sur les providers externes
    /// </summary>
    Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Recherche des artistes sur les providers externes
    /// </summary>
    Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Recherche combinée (chansons, albums, artistes)
    /// </summary>
    Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20);
    
    /// <summary>
    /// Récupère les détails d'une chanson externe
    /// </summary>
    Task<Song?> GetSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Récupère les détails d'un album externe avec ses chansons
    /// </summary>
    Task<Album?> GetAlbumAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Récupère les détails d'un artiste externe
    /// </summary>
    Task<Artist?> GetArtistAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Récupère les albums d'un artiste
    /// </summary>
    Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId);
}
