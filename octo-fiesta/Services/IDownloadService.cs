using octo_fiesta.Models;

namespace octo_fiesta.Services;

/// <summary>
/// Interface pour le service de téléchargement de musique (Deezspot ou autre)
/// </summary>
public interface IDownloadService
{
    /// <summary>
    /// Télécharge une chanson depuis un provider externe
    /// </summary>
    /// <param name="externalProvider">Le provider (deezer, spotify)</param>
    /// <param name="externalId">L'ID sur le provider externe</param>
    /// <param name="cancellationToken">Token d'annulation</param>
    /// <returns>Le chemin du fichier téléchargé</returns>
    Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Télécharge une chanson et stream le résultat au fur et à mesure
    /// </summary>
    /// <param name="externalProvider">Le provider (deezer, spotify)</param>
    /// <param name="externalId">L'ID sur le provider externe</param>
    /// <param name="cancellationToken">Token d'annulation</param>
    /// <returns>Un stream du fichier audio</returns>
    Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Vérifie si une chanson est en cours de téléchargement
    /// </summary>
    DownloadInfo? GetDownloadStatus(string songId);
    
    /// <summary>
    /// Vérifie si le service est correctement configuré et fonctionnel
    /// </summary>
    Task<bool> IsAvailableAsync();
}
