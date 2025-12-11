using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
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
    
    /// <summary>
    /// Parse un ID externe pour extraire le provider, le type et l'ID
    /// Format: ext-{provider}-{type}-{id} (ex: ext-deezer-artist-259, ext-deezer-album-96126, ext-deezer-song-12345)
    /// Also supports legacy format: ext-{provider}-{id} (assumes song type)
    /// </summary>
    (bool isExternal, string? provider, string? type, string? externalId) ParseExternalId(string id);
    
    /// <summary>
    /// Déclenche un scan de la bibliothèque Subsonic
    /// </summary>
    Task<bool> TriggerLibraryScanAsync();
    
    /// <summary>
    /// Récupère le statut actuel du scan
    /// </summary>
    Task<ScanStatus?> GetScanStatusAsync();
}

/// <summary>
/// Implémentation du service de bibliothèque locale
/// Utilise un fichier JSON simple pour stocker les mappings (peut être remplacé par une BDD)
/// </summary>
public class LocalLibraryService : ILocalLibraryService
{
    private readonly string _mappingFilePath;
    private readonly string _downloadDirectory;
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<LocalLibraryService> _logger;
    private Dictionary<string, LocalSongMapping>? _mappings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    // Debounce pour éviter de déclencher trop de scans
    private DateTime _lastScanTrigger = DateTime.MinValue;
    private readonly TimeSpan _scanDebounceInterval = TimeSpan.FromSeconds(30);

    public LocalLibraryService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<LocalLibraryService> logger)
    {
        _downloadDirectory = configuration["Library:DownloadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        _mappingFilePath = Path.Combine(_downloadDirectory, ".mappings.json");
        _httpClient = httpClientFactory.CreateClient();
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
        
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
        var (isExternal, provider, type, externalId) = ParseExternalId(songId);
        return (isExternal, provider, externalId);
    }

    public (bool isExternal, string? provider, string? type, string? externalId) ParseExternalId(string id)
    {
        if (!id.StartsWith("ext-"))
        {
            return (false, null, null, null);
        }
        
        var parts = id.Split('-');
        
        // Known types for the new format
        var knownTypes = new HashSet<string> { "song", "album", "artist" };
        
        // New format: ext-{provider}-{type}-{id} (e.g., ext-deezer-artist-259)
        // Only use new format if parts[2] is a known type
        if (parts.Length >= 4 && knownTypes.Contains(parts[2]))
        {
            var provider = parts[1];
            var type = parts[2];
            var externalId = string.Join("-", parts.Skip(3)); // Handle IDs with dashes
            return (true, provider, type, externalId);
        }
        
        // Legacy format: ext-{provider}-{id} (assumes "song" type for backward compatibility)
        // This handles both 3-part IDs and 4+ part IDs where parts[2] is NOT a known type
        if (parts.Length >= 3)
        {
            var provider = parts[1];
            var externalId = string.Join("-", parts.Skip(2)); // Everything after provider is the ID
            return (true, provider, "song", externalId);
        }
        
        return (false, null, null, null);
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

    public async Task<bool> TriggerLibraryScanAsync()
    {
        // Debounce: éviter de déclencher trop de scans successifs
        var now = DateTime.UtcNow;
        if (now - _lastScanTrigger < _scanDebounceInterval)
        {
            _logger.LogDebug("Scan debounced - last scan was {Elapsed}s ago", 
                (now - _lastScanTrigger).TotalSeconds);
            return true;
        }
        
        _lastScanTrigger = now;
        
        try
        {
            // Appel à l'API Subsonic pour déclencher un scan
            // Note: Les credentials doivent être passés en paramètres (u, p ou t+s)
            var url = $"{_subsonicSettings.Url}/rest/startScan?f=json";
            
            _logger.LogInformation("Triggering Subsonic library scan...");
            
            var response = await _httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("Subsonic scan triggered successfully: {Response}", content);
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to trigger Subsonic scan: {StatusCode}", response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Subsonic library scan");
            return false;
        }
    }

    public async Task<ScanStatus?> GetScanStatusAsync()
    {
        try
        {
            var url = $"{_subsonicSettings.Url}/rest/getScanStatus?f=json";
            
            var response = await _httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                
                if (doc.RootElement.TryGetProperty("subsonic-response", out var subsonicResponse) &&
                    subsonicResponse.TryGetProperty("scanStatus", out var scanStatus))
                {
                    return new ScanStatus
                    {
                        Scanning = scanStatus.TryGetProperty("scanning", out var scanning) && scanning.GetBoolean(),
                        Count = scanStatus.TryGetProperty("count", out var count) ? count.GetInt32() : null
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Subsonic scan status");
        }
        
        return null;
    }
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
