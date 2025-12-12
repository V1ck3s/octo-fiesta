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
    public int? DiscNumber { get; set; }
    public int? TotalTracks { get; set; }
    public int? Year { get; set; }
    public string? Genre { get; set; }
    public string? CoverArtUrl { get; set; }
    
    /// <summary>
    /// URL de la cover en haute résolution (pour embedding)
    /// </summary>
    public string? CoverArtUrlLarge { get; set; }
    
    /// <summary>
    /// BPM (beats per minute) si disponible
    /// </summary>
    public int? Bpm { get; set; }
    
    /// <summary>
    /// ISRC (International Standard Recording Code)
    /// </summary>
    public string? Isrc { get; set; }
    
    /// <summary>
    /// Date de sortie complète (format: YYYY-MM-DD)
    /// </summary>
    public string? ReleaseDate { get; set; }
    
    /// <summary>
    /// Nom de l'album artiste (peut différer de l'artiste du track)
    /// </summary>
    public string? AlbumArtist { get; set; }
    
    /// <summary>
    /// Compositeur(s)
    /// </summary>
    public string? Composer { get; set; }
    
    /// <summary>
    /// Label de l'album
    /// </summary>
    public string? Label { get; set; }
    
    /// <summary>
    /// Copyright
    /// </summary>
    public string? Copyright { get; set; }
    
    /// <summary>
    /// Artistes contributeurs (featurings, etc.)
    /// </summary>
    public List<string> Contributors { get; set; } = new();
    
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

/// <summary>
/// Statut du scan de bibliothèque Subsonic
/// </summary>
public class ScanStatus
{
    public bool Scanning { get; set; }
    public int? Count { get; set; }
}
