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
    private readonly IMusicMetadataService _metadataService;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<LocalLibraryService> _logger;
    private Dictionary<string, LocalSongMapping>? _mappings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    // Debounce to avoid triggering too many scans
    private DateTime _lastScanTrigger = DateTime.MinValue;
    private readonly TimeSpan _scanDebounceInterval = TimeSpan.FromSeconds(30);
    
    // Primary subsonic auth parameters/credentials from config for server-to-server calls
    private SubsonicCredentials? _subsonicAdminCredentials;

    // Whether the configured admin has admin rights (null = not checked yet)
    private bool? _adminIsAdmin;

    // Secondary subsonic auth parameters/credentials for server-to-server calls
    private SubsonicCredentials? _subsonicUserCredentials;
    
    // Whether the captured user has admin rights (null = not checked yet)
    private bool? _userIsAdmin;

    public LocalLibraryService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<LocalLibraryService> logger)
    {
        _downloadDirectory = configuration["Library:DownloadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        _mappingFilePath = Path.Combine(_downloadDirectory, ".mappings.json");
        _httpClient = httpClientFactory.CreateClient();
        _metadataService = metadataService;
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
        var adminCredentialsParameters = new Dictionary<string, string>
        {
            ["u"] = _subsonicSettings.AdminUsername ?? "",
            ["p"] = _subsonicSettings.AdminPassword ?? "",
            ["v"] = "1.16.1",
            ["c"] = "octo-fiesta"
        };
        _subsonicAdminCredentials = SubsonicCredentials.TryFromDictionary(adminCredentialsParameters);
        
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

        mappings.TryGetValue(key, out var mapping);
        if (mapping != null && !File.Exists(mapping.LocalPath))
        {
            mapping = null;
        }

        if (!string.IsNullOrEmpty(mapping?.LocalSubsonicId))
        {
            return mapping.LocalSubsonicId;
        }

        try
        {
            string? title;
            string? artist;
            string? album;

            if (mapping != null)
            {
                title = mapping.Title;
                artist = mapping.Artist;
                album = mapping.Album;
            }
            else
            {
                var externalSong = await _metadataService.GetSongAsync(externalProvider, externalId);
                if (externalSong == null)
                {
                    return null;
                }

                title = externalSong.Title;
                artist = externalSong.Artist;
                album = externalSong.Album;
            }

            var queryText = string.Join(" ", new[] { artist, title });
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return null;
            }

            var authQuery = BuildAuthQuery(_subsonicUserCredentials);
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

            var titleKey = StringNormalizer.CreateComparisonKey(title);
            var artistKey = StringNormalizer.CreateComparisonKey(artist);
            var albumKey = StringNormalizer.CreateComparisonKey(album);

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

            if (mapping != null)
            {
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

    public async Task<string?> WaitForLocalIdAfterScanAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var localId = await GetLocalIdForExternalSongAsync(externalProvider, externalId);
        if (!string.IsNullOrEmpty(localId))
        {
            return localId;
        }

        var now = DateTime.UtcNow;
        var debounceRemaining = _scanDebounceInterval - (now - _lastScanTrigger);

        var scanTriggered = await TriggerLibraryScanAsync();
        if (!scanTriggered)
        {
            return await GetLocalIdForExternalSongAsync(externalProvider, externalId);
        }

        if (debounceRemaining > TimeSpan.Zero)
        {
            var scanStatus = await GetScanStatusAsync();
            if (scanStatus?.Scanning != true)
            {
                _logger.LogInformation(
                    "Library scan was debounced for {Provider}:{ExternalId}; waiting {DelaySeconds}s before retrying",
                    externalProvider,
                    externalId,
                    Math.Ceiling(debounceRemaining.TotalSeconds));

                await Task.Delay(debounceRemaining, cancellationToken);
                scanTriggered = await TriggerLibraryScanAsync();
                if (!scanTriggered)
                {
                    return await GetLocalIdForExternalSongAsync(externalProvider, externalId);
                }
            }
        }

        await WaitForScanToFinishAsync(cancellationToken);
        return await GetLocalIdForExternalSongAsync(externalProvider, externalId);
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

    private async Task<bool> CheckUserIsAdminAsync(SubsonicCredentials? subsonicCredentials)
    {
        try
        {
            var authQuery = BuildAuthQuery(subsonicCredentials);
            if (string.IsNullOrEmpty(authQuery))
            {
                return false;
            }
            
            var url = $"{_subsonicSettings.Url}/rest/getUser?f=json&username={Uri.EscapeDataString(subsonicCredentials!.Username)}{authQuery}";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return false;
            
            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            
            if (doc.RootElement.TryGetProperty("subsonic-response", out var subsonicResponse) &&
                subsonicResponse.TryGetProperty("user", out var user) &&
                user.TryGetProperty("adminRole", out var adminRole))
            {
                var isAdmin = adminRole.GetBoolean();
                if (!isAdmin)
                {
                    _logger.LogDebug("Subsonic user '{User}' has no admin rights", subsonicCredentials.Username);
                }
                return isAdmin;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check user admin rights");
        }
        
        return false;
    }

    public async Task<List<(string PlaylistId, string PlaylistName)>> FindPlaylistsContainingSongAsync(string songId)
    {
        var result = new List<(string PlaylistId, string PlaylistName)>();

        if (string.IsNullOrEmpty(songId))
            return result;

        var authQuery = BuildAuthQuery(_subsonicUserCredentials);
        if (string.IsNullOrEmpty(authQuery))
        {
            _logger.LogWarning("Cannot search playlists: Subsonic credentials not set");
            return result;
        }

        try
        {
            var playlistsUrl = $"{_subsonicSettings.Url}/rest/getPlaylists?f=json{authQuery}";
            var response = await _httpClient.GetAsync(playlistsUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get playlists: {StatusCode}", response.StatusCode);
                return result;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);

            if (!doc.RootElement.TryGetProperty("subsonic-response", out var subResp) ||
                !subResp.TryGetProperty("playlists", out var playlists) ||
                !playlists.TryGetProperty("playlist", out var playlistNode))
                return result;

            foreach (var playlist in EnumerateJsonElements(playlistNode))
            {
                var playlistId = playlist.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                var playlistName = playlist.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrEmpty(playlistId)) continue;

                var detailUrl = $"{_subsonicSettings.Url}/rest/getPlaylist?f=json&id={Uri.EscapeDataString(playlistId)}{authQuery}";
                var detailResponse = await _httpClient.GetAsync(detailUrl);
                if (!detailResponse.IsSuccessStatusCode) continue;

                var detailContent = await detailResponse.Content.ReadAsStringAsync();
                using var detailDoc = JsonDocument.Parse(detailContent);

                if (!detailDoc.RootElement.TryGetProperty("subsonic-response", out var subResp2) ||
                    !subResp2.TryGetProperty("playlist", out var playlistDetail) ||
                    !playlistDetail.TryGetProperty("entry", out var entries))
                    continue;

                foreach (var entry in EnumerateJsonElements(entries))
                {
                    var entrySongId = entry.TryGetProperty("id", out var songIdEl) ? songIdEl.ToString() : null;
                    if (entrySongId == songId)
                    {
                        result.Add((playlistId, playlistName ?? ""));
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search playlists for song {SongId}", songId);
        }

        return result;
    }

    public async Task MigratePlaylistEntriesAsync(string oldSongId, string newSongId, List<(string PlaylistId, string PlaylistName)> affectedPlaylists)
    {
        if (oldSongId == newSongId || affectedPlaylists.Count == 0)
            return;

        var authQuery = BuildAuthQuery(_subsonicUserCredentials);
        if (string.IsNullOrEmpty(authQuery))
        {
            _logger.LogWarning("Cannot migrate playlists: Subsonic credentials not set");
            return;
        }

        foreach (var (playlistId, playlistName) in affectedPlaylists)
        {
            try
            {
                // Re-fetch current playlist state
                var detailUrl = $"{_subsonicSettings.Url}/rest/getPlaylist?f=json&id={Uri.EscapeDataString(playlistId)}{authQuery}";
                var detailResponse = await _httpClient.GetAsync(detailUrl);
                if (!detailResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch playlist {PlaylistId} for migration: {StatusCode}", playlistId, detailResponse.StatusCode);
                    continue;
                }

                var detailContent = await detailResponse.Content.ReadAsStringAsync();
                using var detailDoc = JsonDocument.Parse(detailContent);

                if (!detailDoc.RootElement.TryGetProperty("subsonic-response", out var subResp) ||
                    !subResp.TryGetProperty("playlist", out var playlistDetail) ||
                    !playlistDetail.TryGetProperty("entry", out var entries))
                    continue;

                var songIds = new List<string>();
                foreach (var entry in EnumerateJsonElements(entries))
                {
                    var id = entry.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
                    if (!string.IsNullOrEmpty(id))
                        songIds.Add(id);
                }

                if (songIds.Count == 0 || !songIds.Contains(oldSongId))
                    continue;

                // Build updated list with replacement
                var updatedSongIds = songIds.Select(id => id == oldSongId ? newSongId : id).ToList();

                // Build updatePlaylist URL: remove all entries + re-add in order
                var url = $"{_subsonicSettings.Url}/rest/updatePlaylist?f=json&playlistId={Uri.EscapeDataString(playlistId)}";
                for (var i = 0; i < songIds.Count; i++)
                    url += $"&songIndexToRemove={i}";
                foreach (var id in updatedSongIds)
                    url += $"&songIdToAdd={Uri.EscapeDataString(id)}";
                url += authQuery;

                var updateResponse = await _httpClient.GetAsync(url);
                if (updateResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Migrated playlist '{PlaylistName}' ({PlaylistId}): replaced song {OldId} with {NewId}",
                        playlistName, playlistId, oldSongId, newSongId);
                }
                else
                {
                    _logger.LogWarning("Failed to update playlist '{PlaylistName}' ({PlaylistId}): {StatusCode}",
                        playlistName, playlistId, updateResponse.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error migrating playlist '{PlaylistName}' ({PlaylistId})", playlistName, playlistId);
            }
        }
    }

    /// <summary>
    /// Enumerates JSON elements that can be either a single object or an array
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateJsonElements(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in node.EnumerateArray())
                yield return element;
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            yield return node;
        }
    }

    private string BuildAuthQuery(SubsonicCredentials? credentials)
    {
        if (credentials == null)
        {
            return string.Empty;
        }
        var parts = new List<string>
        {
            $"u={Uri.EscapeDataString(credentials.Username)}",
            $"v={Uri.EscapeDataString(credentials.ApiVersion)}",
            $"c={Uri.EscapeDataString(credentials.ClientName)}"
        };

        if (!string.IsNullOrWhiteSpace(credentials.Token) && !string.IsNullOrWhiteSpace(credentials.Salt))
        {
            parts.Add($"t={Uri.EscapeDataString(credentials.Token)}");
            parts.Add($"s={Uri.EscapeDataString(credentials.Salt)}");
        }
        else if (!string.IsNullOrWhiteSpace(credentials.Password))
        {
            parts.Add($"p={Uri.EscapeDataString(credentials.Password)}");
        }
        
        return "&" + string.Join("&", parts);
    }

    public string GetDownloadDirectory() => _downloadDirectory;

    public void SetSubsonicCredentials(Dictionary<string, string> parameters)
    {
        if (_subsonicUserCredentials != null) return;
        
        var credentials = SubsonicCredentials.TryFromDictionary(parameters);
        
        if (credentials != null)
        {
            _subsonicUserCredentials = credentials;
            _logger.LogInformation("Subsonic credentials captured for user '{User}'", credentials.Username);
        }
        else
        {
            _logger.LogWarning("Failed to capture subsonic credentials from request parameters. Invalid or empty parameters");
        }
    }

    public async Task<bool> TriggerLibraryScanAsync()
    {
        var requestCredentials = await ResolveAdminCapableCredentialsAsync();
        
        if (requestCredentials == null)
        {
            _logger.LogWarning("Can not trigger library scan due to no available admin credentials");
            return false;
        }
        
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
            var authQuery = BuildAuthQuery(requestCredentials);
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
            // Note: This endpoint works without authentication on most Subsonic/Navidrome servers
            // when called from localhost.
            // current behavior: tries to find admin credentials otherwise tries with not admin credentials
            var requestCredentials = await ResolveAdminCapableCredentialsAsync();

            if (requestCredentials == null) requestCredentials = _subsonicUserCredentials;

            var authQuery = BuildAuthQuery(requestCredentials);
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

    private async Task WaitForScanToFinishAsync(CancellationToken cancellationToken)
    {
        const int pollDelayMilliseconds = 1000;
        const int maxWaitMilliseconds = 120000;

        var startedWaitingAt = DateTime.UtcNow;
        var observedRunningScan = false;

        while (DateTime.UtcNow - startedWaitingAt < TimeSpan.FromMilliseconds(maxWaitMilliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scanStatus = await GetScanStatusAsync();
            if (scanStatus?.Scanning == true)
            {
                observedRunningScan = true;
            }
            else if (observedRunningScan)
            {
                return;
            }
            else if (scanStatus?.Scanning == false)
            {
                return;
            }

            await Task.Delay(pollDelayMilliseconds, cancellationToken);
        }

        _logger.LogWarning("Timed out while waiting for library scan to finish");
    }

    private async Task<SubsonicCredentials?> ResolveAdminCapableCredentialsAsync()
    {
        // Check admin rights on first call
        if (_adminIsAdmin == null)
        {
            _adminIsAdmin = await CheckUserIsAdminAsync(_subsonicAdminCredentials);
        }

        if (_userIsAdmin == null)
        {
            _userIsAdmin = await CheckUserIsAdminAsync(_subsonicUserCredentials);
        }

        if (_subsonicAdminCredentials != null && _adminIsAdmin == true)
        {
            return _subsonicAdminCredentials;
        }
        else if (_subsonicUserCredentials != null && _userIsAdmin == true)
        {
            return _subsonicUserCredentials;
        }
        else
        {
            return null;
        }
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
