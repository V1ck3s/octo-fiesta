using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.Local;

/// <summary>
/// Local library service implementation
/// Uses a simple JSON file to store mappings (can be replaced with a database)
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
    
    // Debounce to avoid triggering too many scans
    private DateTime _lastScanTrigger = DateTime.MinValue;
    private readonly TimeSpan _scanDebounceInterval = TimeSpan.FromSeconds(30);
    
    // Stored Subsonic auth parameters for server-to-server calls
    private Dictionary<string, string>? _subsonicCredentials;

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

public async Task RegisterDownloadedSongAsync(Song song, string localPath, string? downloadedQuality = null)
    {
        if (song.ExternalProvider == null || song.ExternalId == null) return;
        
        // Load mappings first (this acquires the lock internally if needed)
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
    
    public async Task<LocalSongMapping?> GetMappingForExternalSongAsync(string externalProvider, string externalId)
    {
        var mappings = await LoadMappingsAsync();
        var key = $"{externalProvider}:{externalId}";
        
        if (mappings.TryGetValue(key, out var mapping) && File.Exists(mapping.LocalPath))
        {
            return mapping;
        }
        
        return null;
    }

    public async Task<string?> GetLocalIdForExternalSongAsync(string externalProvider, string externalId)
    {
        var mappings = await LoadMappingsAsync();
        var key = $"{externalProvider}:{externalId}";

        if (!mappings.TryGetValue(key, out var mapping) || !File.Exists(mapping.LocalPath))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(mapping.LocalSubsonicId))
        {
            return mapping.LocalSubsonicId;
        }

        try
        {
            var queryText = string.Join(" ", new[] { mapping.Artist, mapping.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return null;
            }

            var authQuery = BuildAuthQuery();
            var searchUrl = $"{_subsonicSettings.Url}/rest/search3?f=json&songCount=10&albumCount=0&artistCount=0&query={Uri.EscapeDataString(queryText)}{authQuery}";

            var response = await _httpClient.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Could not resolve local Subsonic ID for {Provider}:{ExternalId}. search3 returned {StatusCode}",
                    externalProvider, externalId, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            if (!doc.RootElement.TryGetProperty("subsonic-response", out var subsonicResponse) ||
                !subsonicResponse.TryGetProperty("searchResult3", out var searchResult) ||
                !searchResult.TryGetProperty("song", out var songNode))
            {
                return null;
            }

            var titleKey = StringNormalizer.CreateComparisonKey(mapping.Title);
            var artistKey = StringNormalizer.CreateComparisonKey(mapping.Artist);
            var albumKey = StringNormalizer.CreateComparisonKey(mapping.Album);

            string? matchedId = null;

            foreach (var songElement in EnumerateSongs(songNode))
            {
                var candidateId = songElement.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                if (string.IsNullOrEmpty(candidateId))
                {
                    continue;
                }

                var candidateTitleKey = StringNormalizer.CreateComparisonKey(songElement.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null);
                var candidateArtistKey = StringNormalizer.CreateComparisonKey(songElement.TryGetProperty("artist", out var artistEl) ? artistEl.GetString() : null);
                var candidateAlbumKey = StringNormalizer.CreateComparisonKey(songElement.TryGetProperty("album", out var albumEl) ? albumEl.GetString() : null);

                var titleMatches = !string.IsNullOrEmpty(titleKey) && titleKey == candidateTitleKey;
                var artistMatches = !string.IsNullOrEmpty(artistKey) && artistKey == candidateArtistKey;
                var albumMatches = !string.IsNullOrEmpty(albumKey) && albumKey == candidateAlbumKey;

                if ((titleMatches && artistMatches) || (titleMatches && albumMatches))
                {
                    matchedId = candidateId;
                    break;
                }
            }

            if (string.IsNullOrEmpty(matchedId))
            {
                return null;
            }

            await _lock.WaitAsync();
            try
            {
                if (mappings.TryGetValue(key, out var mappingToUpdate))
                {
                    mappingToUpdate.LocalSubsonicId = matchedId;
                    await SaveMappingsAsync(mappings);
                }
            }
            finally
            {
                _lock.Release();
            }

            _logger.LogInformation("Resolved local Subsonic ID {LocalId} for external song {Provider}:{ExternalId}",
                matchedId, externalProvider, externalId);
            return matchedId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve local Subsonic ID for external song {Provider}:{ExternalId}",
                externalProvider, externalId);
            return null;
        }
    }

    private static IEnumerable<JsonElement> EnumerateSongs(JsonElement songNode)
    {
        if (songNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var song in songNode.EnumerateArray())
            {
                yield return song;
            }
        }
        else if (songNode.ValueKind == JsonValueKind.Object)
        {
            yield return songNode;
        }
    }

    public (bool isExternal, string? provider, string? externalId) ParseSongId(string songId)
    {
        var (isExternal, provider, _, externalId) = ParseExternalId(songId);
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
        // Fast path: return cached mappings if available
        if (_mappings != null) return _mappings;
        
        // Slow path: acquire lock to load from file (prevents race condition)
        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
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
        finally
        {
            _lock.Release();
        }
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

    private string BuildAuthQuery()
    {
        if (_subsonicCredentials == null || _subsonicCredentials.Count == 0)
            return string.Empty;
        
        var query = string.Join("&", _subsonicCredentials.Select(kv => 
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"&{query}";
    }

    public string GetDownloadDirectory() => _downloadDirectory;

    public void SetSubsonicCredentials(Dictionary<string, string> parameters)
    {
        if (_subsonicCredentials != null) return;
        
        var authParams = new[] { "u", "t", "s", "v", "c" };
        var credentials = new Dictionary<string, string>();
        
        foreach (var key in authParams)
        {
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                credentials[key] = value;
            }
        }
        
        if (credentials.ContainsKey("u"))
        {
            _subsonicCredentials = credentials;
            _logger.LogInformation("Subsonic credentials captured for user '{User}'", credentials["u"]);
        }
    }

    public async Task<bool> TriggerLibraryScanAsync()
    {
        // Debounce: avoid triggering too many successive scans
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
            var authQuery = BuildAuthQuery();
            var url = $"{_subsonicSettings.Url}/rest/startScan?f=json{authQuery}";
            
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
                _logger.LogWarning("Failed to trigger Subsonic scan: {StatusCode} - Server may require authentication", response.StatusCode);
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
            // Note: This endpoint works without authentication on most Subsonic/Navidrome servers
            // when called from localhost.
            var authQuery = BuildAuthQuery();
            var url = $"{_subsonicSettings.Url}/rest/getScanStatus?f=json{authQuery}";
            
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
/// Represents the mapping between an external song and its local file
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
    
    /// <summary>
    /// Quality of the downloaded file (e.g., "FLAC", "MP3_320", "MP3_128")
    /// Null for legacy downloads before quality tracking was added
    /// </summary>
    public string? DownloadedQuality { get; set; }
}
