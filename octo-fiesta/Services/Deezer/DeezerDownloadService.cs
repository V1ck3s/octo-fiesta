using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Download;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Subsonic;
using Microsoft.Extensions.Options;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.Deezer;

/// <summary>
/// C# port of the DeezerDownloader JavaScript
/// Handles Deezer authentication, track downloading and decryption
/// </summary>
public class DeezerDownloadService : BaseDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    
    private readonly string? _arl;
    private readonly string? _arlFallback;
    private readonly string? _preferredQuality;
    
    private string? _apiToken;
    private string? _licenseToken;
    
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly int _minRequestIntervalMs = 200;
    
    private const string DeezerApiBase = "https://api.deezer.com";
    
    // Deezer's standard Blowfish CBC encryption key for track decryption
    // This is a well-known constant used by the Deezer API, not a user-specific secret
    private const string BfSecret = "g4el58wc0zvf9na1";

    protected override string ProviderName => "deezer";

    public DeezerDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IServiceProvider serviceProvider,
        ILogger<DeezerDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        
        var deezer = deezerSettings.Value;
        _arl = deezer.Arl;
        _arlFallback = deezer.ArlFallback;
        _preferredQuality = deezer.Quality;
    }

    #region BaseDownloadService Implementation

    public override async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrEmpty(_arl))
        {
            Logger.LogWarning("Deezer ARL not configured");
            return false;
        }

        try
        {
            await InitializeAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Deezer service not available");
            return false;
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-deezer-album-";
        if (albumId.StartsWith(prefix))
        {
            return albumId[prefix.Length..];
        }
        return null;
    }
    
    protected override string? GetTargetQuality() => _preferredQuality ?? "FLAC";

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var downloadInfo = await GetTrackDownloadInfoAsync(trackId, cancellationToken);
        
        Logger.LogInformation("Track token obtained for: {Title} - {Artist}", downloadInfo.Title, downloadInfo.Artist);
        Logger.LogInformation("Using format: {Format}", downloadInfo.Format);

        // Determine extension based on format
        var extension = downloadInfo.Format?.ToUpper() switch
        {
            "FLAC" => ".flac",
            _ => ".mp3"
        };
        
        // Determine actual quality for storage
        var downloadedQuality = downloadInfo.Format?.ToUpper() switch
        {
            "FLAC" => "FLAC",
            "MP3_320" => "MP3_320",
            "MP3_128" => "MP3_128",
            _ => downloadInfo.Format
        };

        // Build organized folder structure: Artist/Album/Track using AlbumArtist (fallback to Artist for singles)
        var artistForPath = song.AlbumArtist ?? song.Artist;
        var basePath = SubsonicSettings.StorageMode == StorageMode.Cache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, artistForPath, song.Album, song.Title, song.Track, extension);
        
        // Create directories if they don't exist
        var albumFolder = Path.GetDirectoryName(outputPath)!;
        EnsureDirectoryExists(albumFolder);
        
        // Resolve unique path if file already exists
        outputPath = PathHelper.ResolveUniquePath(outputPath);

        // Download the encrypted file
        var response = await RetryWithBackoffAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DownloadUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
            request.Headers.Add("Accept", "*/*");
            
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        });

        response.EnsureSuccessStatusCode();

        // Download and decrypt
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = IOFile.Create(outputPath);
        
        // Use the actual track ID from downloadInfo for decryption (may be alternative track)
        await DecryptAndWriteStreamAsync(responseStream, outputFile, downloadInfo.TrackId, cancellationToken);
        
        // Close file before writing metadata
        await outputFile.DisposeAsync();
        
        // Write metadata and cover art
        await WriteMetadataAsync(outputPath, song, cancellationToken);

        return new DownloadResult(outputPath, downloadedQuality);
    }

    #endregion

    #region Deezer API Methods

    private async Task InitializeAsync(string? arlOverride = null)
    {
        var arl = arlOverride ?? _arl;
        if (string.IsNullOrEmpty(arl))
        {
            throw new Exception("ARL token required for Deezer downloads");
        }

        await RetryWithBackoffAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, 
                "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
            
            request.Headers.Add("Cookie", $"arl={arl}");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.TryGetProperty("checkForm", out var checkForm))
            {
                _apiToken = checkForm.GetString();
                
                if (results.TryGetProperty("USER", out var user) &&
                    user.TryGetProperty("OPTIONS", out var options) &&
                    options.TryGetProperty("license_token", out var licenseToken))
                {
                    _licenseToken = licenseToken.GetString();
                }
                
                Logger.LogInformation("Deezer token refreshed successfully");
                return true;
            }

            throw new Exception("Invalid ARL token");
        });
    }

    private async Task<TrackDownloadInfo> GetTrackDownloadInfoAsync(string trackId, CancellationToken cancellationToken)
    {
        return await GetTrackDownloadInfoInternalAsync(trackId, _arl!, isRetryWithFallback: false, cancellationToken);
    }

    private async Task<TrackDownloadInfo> GetTrackDownloadInfoInternalAsync(
        string trackId, 
        string arl, 
        bool isRetryWithFallback,
        CancellationToken cancellationToken)
    {
        // Refresh token with specific ARL
        await InitializeAsync(arl);

        return await QueueRequestAsync(async () =>
        {
            // Get track info
            var trackResponse = await _httpClient.GetAsync($"{DeezerApiBase}/track/{trackId}", cancellationToken);
            trackResponse.EnsureSuccessStatusCode();
            
            var trackJson = await trackResponse.Content.ReadAsStringAsync(cancellationToken);
            var trackDoc = JsonDocument.Parse(trackJson);
            
            // Track ID used for decryption (may change if using alternative)
            var decryptionTrackId = trackId;
            
            var title = trackDoc.RootElement.GetProperty("title").GetString() ?? "";
            var artist = trackDoc.RootElement.TryGetProperty("artist", out var artistEl) 
                ? artistEl.GetProperty("name").GetString() ?? "" 
                : "";
            
            // Check if track is readable (available in user's region)
            var isReadable = trackDoc.RootElement.TryGetProperty("readable", out var readableEl) 
                && readableEl.GetBoolean();
            
            // If track is not readable, try to find an alternative using enhanced fallbacks
            if (!isReadable)
            {
                Logger.LogWarning("Track {TrackId} ({Title} - {Artist}) is not readable, searching for alternative...", 
                    trackId, title, artist);
                
                var alternativeTrackId = await FindAlternativeTrackWithFallbacksAsync(trackId, title, artist, arl, cancellationToken);
                if (alternativeTrackId != null)
                {
                    Logger.LogInformation("Found alternative track: {AlternativeId}", alternativeTrackId);
                    
                    // Update decryption track ID to use the alternative
                    decryptionTrackId = alternativeTrackId;
                    
                    // Get the alternative track info
                    var altResponse = await _httpClient.GetAsync($"{DeezerApiBase}/track/{alternativeTrackId}", cancellationToken);
                    altResponse.EnsureSuccessStatusCode();
                    
                    var altJson = await altResponse.Content.ReadAsStringAsync(cancellationToken);
                    trackDoc = JsonDocument.Parse(altJson);
                }
                else
                {
                    throw new Exception($"Track is not available in your region and no alternative found for: {title} - {artist}");
                }
            }
            
            if (!trackDoc.RootElement.TryGetProperty("track_token", out var trackTokenElement))
            {
                throw new Exception("Track not found or track_token missing");
            }

            var trackToken = trackTokenElement.GetString();

            // Get download URL via media API
            // Build format list based on preferred quality
            var formatsList = BuildFormatsList(_preferredQuality);
            
            var mediaRequest = new
            {
                license_token = _licenseToken,
                media = new[]
                {
                    new
                    {
                        type = "FULL",
                        formats = formatsList
                    }
                },
                track_tokens = new[] { trackToken }
            };

            var mediaHttpRequest = new HttpRequestMessage(HttpMethod.Post, "https://media.deezer.com/v1/get_url");
            mediaHttpRequest.Headers.Add("Cookie", $"arl={arl}");
            mediaHttpRequest.Content = new StringContent(
                JsonSerializer.Serialize(mediaRequest), 
                Encoding.UTF8, 
                "application/json");

            using (mediaHttpRequest)
            {
                var mediaResponse = await _httpClient.SendAsync(mediaHttpRequest, cancellationToken);
                mediaResponse.EnsureSuccessStatusCode();

                var mediaJson = await mediaResponse.Content.ReadAsStringAsync(cancellationToken);
                var mediaDoc = JsonDocument.Parse(mediaJson);

                if (!mediaDoc.RootElement.TryGetProperty("data", out var data) || 
                    data.GetArrayLength() == 0)
                {
                    throw new Exception("No download URL available");
                }

                var firstData = data[0];
                var hasMedia = firstData.TryGetProperty("media", out var media) && media.GetArrayLength() > 0;

                // If no media available and this is the first attempt, try fallbacks
                if (!hasMedia && !isRetryWithFallback)
                {
                    Logger.LogWarning("Track {TrackId} returned no media sources (readable={IsReadable}), trying fallbacks...", 
                        trackId, isReadable);
                    
                    var alternativeTrackId = await FindAlternativeTrackWithFallbacksAsync(trackId, title, artist, arl, cancellationToken);
                    if (alternativeTrackId != null && alternativeTrackId != trackId)
                    {
                        Logger.LogInformation("Retrying with alternative track: {AlternativeId}", alternativeTrackId);
                        return await GetTrackDownloadInfoInternalAsync(alternativeTrackId, arl, isRetryWithFallback: true, cancellationToken);
                    }
                    
                    // If no alternative found but we have a fallback ARL, try that
                    if (!string.IsNullOrEmpty(_arlFallback) && arl != _arlFallback)
                    {
                        Logger.LogWarning("No alternative found, trying fallback ARL...");
                        return await GetTrackDownloadInfoInternalAsync(trackId, _arlFallback, isRetryWithFallback: true, cancellationToken);
                    }
                    
                    throw new Exception("No media sources available - track may be unavailable in your region");
                }

                if (!hasMedia)
                {
                    throw new Exception("No media sources available - track may be unavailable in your region");
                }

                // Build a dictionary of available formats
                var availableFormats = new Dictionary<string, string>();
                foreach (var mediaItem in media.EnumerateArray())
                {
                    if (mediaItem.TryGetProperty("format", out var formatEl) &&
                        mediaItem.TryGetProperty("sources", out var sources) && 
                        sources.GetArrayLength() > 0)
                    {
                        var fmt = formatEl.GetString();
                        var url = sources[0].GetProperty("url").GetString();
                        if (!string.IsNullOrEmpty(fmt) && !string.IsNullOrEmpty(url))
                        {
                            availableFormats[fmt] = url;
                        }
                    }
                }

                if (availableFormats.Count == 0)
                {
                    throw new Exception("No download URL found in media sources - track may be region locked");
                }

                // Log available formats for debugging
                Logger.LogInformation("Available formats from Deezer: {Formats}", string.Join(", ", availableFormats.Keys));

                // Quality priority order (highest to lowest)
                var qualityPriority = new[] { "FLAC", "MP3_320", "MP3_128" };
                
                string? selectedFormat = null;
                string? downloadUrl = null;

                // Select the best available quality from what Deezer returned
                foreach (var quality in qualityPriority)
                {
                    if (availableFormats.TryGetValue(quality, out var url))
                    {
                        selectedFormat = quality;
                        downloadUrl = url;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    throw new Exception("No compatible format found in available media sources");
                }

                Logger.LogInformation("Selected quality: {Format}", selectedFormat);

                return new TrackDownloadInfo
                {
                    DownloadUrl = downloadUrl,
                    Format = selectedFormat ?? "MP3_128",
                    Title = title,
                    Artist = artist,
                    TrackId = decryptionTrackId
                };
            }
        });
    }

    #endregion

    #region Decryption

    private byte[] GetBlowfishKey(string trackId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(trackId));
        var hashHex = Convert.ToHexString(hash).ToLower();
        
        var bfKey = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            bfKey[i] = (byte)(hashHex[i] ^ hashHex[i + 16] ^ BfSecret[i]);
        }
        
        return bfKey;
    }

    private async Task DecryptAndWriteStreamAsync(
        Stream input, 
        Stream output, 
        string trackId, 
        CancellationToken cancellationToken)
    {
        var bfKey = GetBlowfishKey(trackId);
        var iv = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        
        var buffer = new byte[2048];
        int chunkIndex = 0;
        
        while (true)
        {
            var bytesRead = await ReadExactAsync(input, buffer, cancellationToken);
            if (bytesRead == 0) break;

            var chunk = buffer.AsSpan(0, bytesRead).ToArray();

            // Every 3rd chunk (index % 3 == 0) is encrypted
            if (chunkIndex % 3 == 0 && bytesRead == 2048)
            {
                chunk = DecryptBlowfishCbc(chunk, bfKey, iv);
            }

            await output.WriteAsync(chunk, cancellationToken);
            chunkIndex++;
        }
    }

    private async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (bytesRead == 0) break;
            totalRead += bytesRead;
        }
        return totalRead;
    }

    private byte[] DecryptBlowfishCbc(byte[] data, byte[] key, byte[] iv)
    {
        // Use BouncyCastle for native Blowfish CBC decryption
        var engine = new BlowfishEngine();
        var cipher = new CbcBlockCipher(engine);
        cipher.Init(false, new ParametersWithIV(new KeyParameter(key), iv));
        
        var output = new byte[data.Length];
        var blockSize = cipher.GetBlockSize(); // 8 bytes for Blowfish
        
        for (int offset = 0; offset < data.Length; offset += blockSize)
        {
            cipher.ProcessBlock(data, offset, output, offset);
        }
        
        return output;
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Builds the list of formats to request from Deezer based on preferred quality.
    /// </summary>
    private static object[] BuildFormatsList(string? preferredQuality)
    {
        var allFormats = new[]
        {
            new { cipher = "BF_CBC_STRIPE", format = "FLAC" },
            new { cipher = "BF_CBC_STRIPE", format = "MP3_320" },
            new { cipher = "BF_CBC_STRIPE", format = "MP3_128" }
        };

        if (string.IsNullOrEmpty(preferredQuality))
        {
            return allFormats;
        }

        var preferred = preferredQuality.ToUpperInvariant();
        
        return preferred switch
        {
            "FLAC" => allFormats,
            "MP3_320" => new object[]
            {
                new { cipher = "BF_CBC_STRIPE", format = "MP3_320" },
                new { cipher = "BF_CBC_STRIPE", format = "MP3_128" }
            },
            "MP3_128" => new object[]
            {
                new { cipher = "BF_CBC_STRIPE", format = "MP3_128" }
            },
            _ => allFormats
        };
    }

    private async Task<T> RetryWithBackoffAsync<T>(Func<Task<T>> action, int maxRetries = 3, int initialDelayMs = 1000)
    {
        Exception? lastException = null;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                                                   ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lastException = ex;
                if (attempt < maxRetries - 1)
                {
                    var delay = initialDelayMs * (int)Math.Pow(2, attempt);
                    Logger.LogWarning("Retry attempt {Attempt}/{MaxRetries} after {Delay}ms ({Message})", 
                        attempt + 1, maxRetries, delay, ex.Message);
                    await Task.Delay(delay);
                }
            }
            catch
            {
                throw;
            }
        }

        throw lastException!;
    }

    private async Task RetryWithBackoffAsync(Func<Task<bool>> action, int maxRetries = 3, int initialDelayMs = 1000)
    {
        await RetryWithBackoffAsync<bool>(action, maxRetries, initialDelayMs);
    }

    private async Task<T> QueueRequestAsync<T>(Func<Task<T>> action)
    {
        await _requestLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var timeSinceLastRequest = (now - _lastRequestTime).TotalMilliseconds;
            
            if (timeSinceLastRequest < _minRequestIntervalMs)
            {
                await Task.Delay((int)(_minRequestIntervalMs - timeSinceLastRequest));
            }

            _lastRequestTime = DateTime.UtcNow;
            return await action();
        }
        finally
        {
            _requestLock.Release();
        }
    }

    #endregion

    #region Alternative Track Search

    /// <summary>
    /// Data returned from the private Deezer API (deezer.pageTrack)
    /// </summary>
    private class TrackPageData
    {
        public string? FallbackId { get; set; }
        public string? Isrc { get; set; }
        public string? TrackToken { get; set; }
    }

    /// <summary>
    /// Gets track data from the private Deezer API (deezer.pageTrack)
    /// This returns FALLBACK_ID and ISRC which are not available in the public API
    /// </summary>
    private async Task<TrackPageData?> GetTrackPageDataAsync(string trackId, string arl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://www.deezer.com/ajax/gw-light.php?method=deezer.pageTrack&input=3&api_version=1.0&api_token={_apiToken}");
            
            request.Headers.Add("Cookie", $"arl={arl}");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { SNG_ID = trackId }),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            // Check for error - only treat as error if it has actual error content
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                // Empty error array is not an actual error
                if (error.ValueKind != JsonValueKind.Array || error.GetArrayLength() > 0)
                {
                    Logger.LogWarning("pageTrack API error for {TrackId}: {Error}", trackId, error.ToString());
                    return null;
                }
            }

            if (!doc.RootElement.TryGetProperty("results", out var results))
            {
                Logger.LogWarning("pageTrack returned no results for {TrackId}", trackId);
                return null;
            }

            var data = new TrackPageData();

            // Extract DATA section
            if (results.TryGetProperty("DATA", out var trackData))
            {
                // Get FALLBACK.SNG_ID if available
                if (trackData.TryGetProperty("FALLBACK", out var fallback) &&
                    fallback.TryGetProperty("SNG_ID", out var fallbackId))
                {
                    data.FallbackId = fallbackId.GetString();
                }

                // Get ISRC
                if (trackData.TryGetProperty("ISRC", out var isrc))
                {
                    data.Isrc = isrc.GetString();
                }

                // Get TRACK_TOKEN
                if (trackData.TryGetProperty("TRACK_TOKEN", out var trackToken))
                {
                    data.TrackToken = trackToken.GetString();
                }
            }

            return data;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error getting track page data for {TrackId}", trackId);
            return null;
        }
    }

    /// <summary>
    /// Searches for a track by ISRC code
    /// </summary>
    private async Task<string?> FindTrackByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            var searchResponse = await _httpClient.GetAsync(
                $"{DeezerApiBase}/track/isrc:{isrc}", 
                cancellationToken);
            
            if (!searchResponse.IsSuccessStatusCode)
                return null;
            
            var searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
            var searchDoc = JsonDocument.Parse(searchJson);
            
            // Check if we got a valid track (not an error)
            if (searchDoc.RootElement.TryGetProperty("error", out _))
                return null;
            
            // Check if track is readable
            var isReadable = searchDoc.RootElement.TryGetProperty("readable", out var readableEl) 
                && readableEl.GetBoolean();
            
            if (!isReadable)
                return null;
            
            if (searchDoc.RootElement.TryGetProperty("id", out var idEl))
            {
                var trackId = idEl.GetInt64().ToString();
                var title = searchDoc.RootElement.TryGetProperty("title", out var titleEl) 
                    ? titleEl.GetString() : "Unknown";
                Logger.LogInformation("Found track by ISRC {Isrc}: {Title} (ID: {Id})", isrc, title, trackId);
                return trackId;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error searching track by ISRC {Isrc}", isrc);
            return null;
        }
    }

    /// <summary>
    /// Tries to find an alternative track using multiple fallback strategies:
    /// 1. FALLBACK_ID from Deezer's private API
    /// 2. ISRC search
    /// 3. Title/Artist search
    /// </summary>
    private async Task<string?> FindAlternativeTrackWithFallbacksAsync(
        string originalTrackId,
        string title, 
        string artist,
        string arl,
        CancellationToken cancellationToken)
    {
        // Strategy 1: Try FALLBACK_ID from private API
        var pageData = await GetTrackPageDataAsync(originalTrackId, arl, cancellationToken);
        
        if (pageData?.FallbackId != null && pageData.FallbackId != "0" && pageData.FallbackId != originalTrackId)
        {
            Logger.LogInformation("Using Deezer FALLBACK_ID: {FallbackId} for track {TrackId}", 
                pageData.FallbackId, originalTrackId);
            return pageData.FallbackId;
        }

        // Strategy 2: Try ISRC search
        if (!string.IsNullOrEmpty(pageData?.Isrc))
        {
            var isrcTrackId = await FindTrackByIsrcAsync(pageData.Isrc, cancellationToken);
            if (isrcTrackId != null && isrcTrackId != originalTrackId)
            {
                Logger.LogInformation("Found alternative via ISRC {Isrc}: {AlternativeId}", 
                    pageData.Isrc, isrcTrackId);
                return isrcTrackId;
            }
        }

        // Strategy 3: Fall back to title/artist search
        Logger.LogInformation("Trying title/artist search fallback for: {Title} - {Artist}", title, artist);
        return await FindAlternativeTrackAsync(title, artist, cancellationToken);
    }

    /// <summary>
    /// Searches for an alternative track when the original is not available (readable: false)
    /// </summary>
    private async Task<string?> FindAlternativeTrackAsync(string title, string artist, CancellationToken cancellationToken)
    {
        try
        {
            // Normalize title by removing common suffixes in parentheses
            var normalizedTitle = NormalizeTitle(title);
            
            var searchQuery = Uri.EscapeDataString($"{normalizedTitle} {artist}");
            var searchResponse = await _httpClient.GetAsync($"{DeezerApiBase}/search/track?q={searchQuery}&limit=10", cancellationToken);
            searchResponse.EnsureSuccessStatusCode();
            
            var searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
            var searchDoc = JsonDocument.Parse(searchJson);
            
            if (!searchDoc.RootElement.TryGetProperty("data", out var data))
                return null;
            
            // Find the first readable track that matches title and artist (case-insensitive)
            foreach (var track in data.EnumerateArray())
            {
                var isReadable = track.TryGetProperty("readable", out var readableEl) && readableEl.GetBoolean();
                if (!isReadable)
                    continue;
                
                var trackTitle = track.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var trackArtist = track.TryGetProperty("artist", out var artistEl) 
                    ? (artistEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null) 
                    : null;
                
                // Check if title matches (exact or normalized)
                if (trackTitle != null && 
                    (string.Equals(NormalizeTitle(trackTitle), normalizedTitle, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(trackTitle, title, StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(artist, trackArtist, StringComparison.OrdinalIgnoreCase))
                {
                    var trackId = track.GetProperty("id").GetInt64().ToString();
                    Logger.LogInformation("Found alternative: {Title} by {Artist} (ID: {Id})", trackTitle, trackArtist, trackId);
                    return trackId;
                }
            }
            
            // If exact match not found, try a more lenient match (just title contains)
            foreach (var track in data.EnumerateArray())
            {
                var isReadable = track.TryGetProperty("readable", out var readableEl) && readableEl.GetBoolean();
                if (!isReadable)
                    continue;
                
                var trackTitle = track.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var trackArtist = track.TryGetProperty("artist", out var artistEl) 
                    ? (artistEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null) 
                    : null;
                
                if (trackTitle != null && 
                    trackTitle.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(artist, trackArtist, StringComparison.OrdinalIgnoreCase))
                {
                    var trackId = track.GetProperty("id").GetInt64().ToString();
                    Logger.LogInformation("Found alternative (lenient match): {Title} by {Artist} (ID: {Id})", trackTitle, trackArtist, trackId);
                    return trackId;
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error searching for alternative track");
            return null;
        }
    }

    /// <summary>
    /// Normalizes a track title by removing common suffixes in parentheses
    /// e.g., "Danger Zone (From Top Gun)" -> "Danger Zone"
    /// </summary>
    private static string NormalizeTitle(string title)
    {
        // Remove content in parentheses at the end: "Song (From Album)" -> "Song"
        var parenIndex = title.IndexOf('(');
        if (parenIndex > 0)
        {
            return title.Substring(0, parenIndex).Trim();
        }
        return title;
    }

    #endregion

    private class TrackDownloadInfo
    {
        public string DownloadUrl { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string TrackId { get; set; } = string.Empty;
    }
}
