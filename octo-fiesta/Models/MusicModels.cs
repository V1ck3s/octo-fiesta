namespace octo_fiesta.Models;

/// <summary>
/// Représente une chanson (locale ou externe)
/// </summary>
public class Song
{
    /// <summary>
    /// ID unique. Pour les chansons externes, préfixé avec "ext-" + provider + "-" + id externe
    /// Exemple: "ext-deezer-123456" ou "local-789"
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? ArtistId { get; set; }
    public string Album { get; set; } = string.Empty;
    public string? AlbumId { get; set; }
    public int? Duration { get; set; } // En secondes
    public int? Track { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }
    public string? CoverArtUrl { get; set; }
    
    /// <summary>
    /// Indique si la chanson est disponible localement ou doit être téléchargée
    /// </summary>
    public bool IsLocal { get; set; }
    
    /// <summary>
    /// Provider externe (deezer, spotify, etc.) - null si local
    /// </summary>
    public string? ExternalProvider { get; set; }
    
    /// <summary>
    /// ID sur le provider externe (pour le téléchargement)
    /// </summary>
    public string? ExternalId { get; set; }
    
    /// <summary>
    /// Chemin du fichier local (si disponible)
    /// </summary>
    public string? LocalPath { get; set; }
}

/// <summary>
/// Représente un artiste
/// </summary>
public class Artist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public int? AlbumCount { get; set; }
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
}

/// <summary>
/// Représente un album
/// </summary>
public class Album
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? ArtistId { get; set; }
    public int? Year { get; set; }
    public int? SongCount { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? Genre { get; set; }
    public bool IsLocal { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
    public List<Song> Songs { get; set; } = new();
}

/// <summary>
/// Résultat de recherche combinant résultats locaux et externes
/// </summary>
public class SearchResult
{
    public List<Song> Songs { get; set; } = new();
    public List<Album> Albums { get; set; } = new();
    public List<Artist> Artists { get; set; } = new();
}

/// <summary>
/// État du téléchargement d'une chanson
/// </summary>
public enum DownloadStatus
{
    NotStarted,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// Information sur un téléchargement en cours ou terminé
/// </summary>
public class DownloadInfo
{
    public string SongId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string ExternalProvider { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; }
    public double Progress { get; set; } // 0.0 à 1.0
    public string? LocalPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
