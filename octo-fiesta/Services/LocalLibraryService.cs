using octo_fiesta.Models;

namespace octo_fiesta.Services;

/// <summary>
/// Interface pour la gestion de la bibliothèque locale de musiques
/// </summary>
public interface ILocalLibraryService
{
    /// <summary>
    /// Vérifie si une chanson externe existe déjà localement
    /// </summary>
    Task<string?> GetLocalPathForExternalSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Enregistre une chanson téléchargée dans la bibliothèque locale
    /// </summary>
    Task RegisterDownloadedSongAsync(Song song, string localPath);
    
    /// <summary>
    /// Récupère le mapping entre ID externe et ID local
    /// </summary>
    Task<string?> GetLocalIdForExternalSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Parse un ID de chanson pour déterminer s'il est externe ou local
    /// </summary>
    (bool isExternal, string? provider, string? externalId) ParseSongId(string songId);
}

/// <summary>
/// Implémentation du service de bibliothèque locale
/// Utilise un fichier JSON simple pour stocker les mappings (peut être remplacé par une BDD)
/// </summary>
public class LocalLibraryService : ILocalLibraryService
{
    private readonly string _mappingFilePath;
    private readonly string _downloadDirectory;
    private Dictionary<string, LocalSongMapping>? _mappings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LocalLibraryService(IConfiguration configuration)
    {
        _downloadDirectory = configuration["Library:DownloadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        _mappingFilePath = Path.Combine(_downloadDirectory, ".mappings.json");
        
        if (!Directory.Exists(_downloadDirectory))
        {
            Directory.CreateDirectory(_downloadDirectory);
        }
    }

    public async Task<string?> GetLocalPathForExternalSongAsync(string externalProvider, string externalId)
    {
        var mappings = await LoadMappingsAsync();
        var key = $"{externalProvider}:{externalId}";
        
        if (mappings.TryGetValue(key, out var mapping) && File.Exists(mapping.LocalPath))
        {
            return mapping.LocalPath;
        }
        
        return null;
    }

    public async Task RegisterDownloadedSongAsync(Song song, string localPath)
    {
        if (song.ExternalProvider == null || song.ExternalId == null) return;
        
        await _lock.WaitAsync();
        try
        {
            var mappings = await LoadMappingsAsync();
            var key = $"{song.ExternalProvider}:{song.ExternalId}";
            
            mappings[key] = new LocalSongMapping
            {
                ExternalProvider = song.ExternalProvider,
                ExternalId = song.ExternalId,
                LocalPath = localPath,
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                DownloadedAt = DateTime.UtcNow
            };
            
            await SaveMappingsAsync(mappings);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetLocalIdForExternalSongAsync(string externalProvider, string externalId)
    {
        // Pour l'instant, on retourne null car on n'a pas encore d'intégration
        // avec le serveur Subsonic pour récupérer l'ID local après scan
        await Task.CompletedTask;
        return null;
    }

    public (bool isExternal, string? provider, string? externalId) ParseSongId(string songId)
    {
        if (songId.StartsWith("ext-"))
        {
            var parts = songId.Split('-', 3);
            if (parts.Length == 3)
            {
                return (true, parts[1], parts[2]);
            }
        }
        
        return (false, null, null);
    }

    private async Task<Dictionary<string, LocalSongMapping>> LoadMappingsAsync()
    {
        if (_mappings != null) return _mappings;
        
        if (File.Exists(_mappingFilePath))
        {
            var json = await File.ReadAllTextAsync(_mappingFilePath);
            _mappings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, LocalSongMapping>>(json) 
                        ?? new Dictionary<string, LocalSongMapping>();
        }
        else
        {
            _mappings = new Dictionary<string, LocalSongMapping>();
        }
        
        return _mappings;
    }

    private async Task SaveMappingsAsync(Dictionary<string, LocalSongMapping> mappings)
    {
        _mappings = mappings;
        var json = System.Text.Json.JsonSerializer.Serialize(mappings, new System.Text.Json.JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        await File.WriteAllTextAsync(_mappingFilePath, json);
    }

    public string GetDownloadDirectory() => _downloadDirectory;
}

/// <summary>
/// Représente le mapping entre une chanson externe et son fichier local
/// </summary>
public class LocalSongMapping
{
    public string ExternalProvider { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string? LocalSubsonicId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public DateTime DownloadedAt { get; set; }
}
