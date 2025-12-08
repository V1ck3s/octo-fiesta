using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using octo_fiesta.Models;

namespace octo_fiesta.Services;

/// <summary>
/// Configuration pour le téléchargeur Deezer
/// </summary>
public class DeezerDownloaderSettings
{
    public string? Arl { get; set; }
    public string? ArlFallback { get; set; }
    public string DownloadPath { get; set; } = "./downloads";
}

/// <summary>
/// Port C# du DeezerDownloader JavaScript
/// Gère l'authentification Deezer, le téléchargement et le déchiffrement des pistes
/// </summary>
public class DeezerDownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IMusicMetadataService _metadataService;
    private readonly ILogger<DeezerDownloadService> _logger;
    
    private readonly string _downloadPath;
    private readonly string? _arl;
    private readonly string? _arlFallback;
    
    private string? _apiToken;
    private string? _licenseToken;
    private bool _usingFallback;
    
    private readonly Dictionary<string, DownloadInfo> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly int _minRequestIntervalMs = 200;
    
    private const string DeezerApiBase = "https://api.deezer.com";
    private const string BfSecret = "g4el58wc0zvf9na1";

    public DeezerDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        ILogger<DeezerDownloadService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _configuration = configuration;
        _localLibraryService = localLibraryService;
        _metadataService = metadataService;
        _logger = logger;
        
        _downloadPath = configuration["Library:DownloadPath"] ?? "./downloads";
        _arl = configuration["Deezer:Arl"];
        _arlFallback = configuration["Deezer:ArlFallback"];
        
        if (!Directory.Exists(_downloadPath))
        {
            Directory.CreateDirectory(_downloadPath);
        }
    }

    #region IDownloadService Implementation

    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "deezer")
        {
            throw new NotSupportedException($"Provider '{externalProvider}' is not supported");
        }

        var songId = $"ext-{externalProvider}-{externalId}";
        
        // Vérifier si déjà téléchargé
        var existingPath = await _localLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (existingPath != null && File.Exists(existingPath))
        {
            _logger.LogInformation("Song already downloaded: {Path}", existingPath);
            return existingPath;
        }

        // Vérifier si téléchargement en cours
        if (_activeDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
        {
            _logger.LogInformation("Download already in progress for {SongId}", songId);
            while (activeDownload.Status == DownloadStatus.InProgress)
            {
                await Task.Delay(500, cancellationToken);
            }
            
            if (activeDownload.Status == DownloadStatus.Completed && activeDownload.LocalPath != null)
            {
                return activeDownload.LocalPath;
            }
            
            throw new Exception(activeDownload.ErrorMessage ?? "Download failed");
        }

        await _downloadLock.WaitAsync(cancellationToken);
        try
        {
            // Récupérer les métadonnées
            var song = await _metadataService.GetSongAsync(externalProvider, externalId);
            if (song == null)
            {
                throw new Exception("Song not found");
            }

            var downloadInfo = new DownloadInfo
            {
                SongId = songId,
                ExternalId = externalId,
                ExternalProvider = externalProvider,
                Status = DownloadStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };
            _activeDownloads[songId] = downloadInfo;

            try
            {
                var localPath = await DownloadTrackAsync(externalId, song, cancellationToken);
                
                downloadInfo.Status = DownloadStatus.Completed;
                downloadInfo.LocalPath = localPath;
                downloadInfo.CompletedAt = DateTime.UtcNow;
                
                song.LocalPath = localPath;
                await _localLibraryService.RegisterDownloadedSongAsync(song, localPath);
                
                _logger.LogInformation("Download completed: {Path}", localPath);
                return localPath;
            }
            catch (Exception ex)
            {
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Download failed for {SongId}", songId);
                throw;
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    public async Task<Stream> DownloadAndStreamAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var localPath = await DownloadSongAsync(externalProvider, externalId, cancellationToken);
        return File.OpenRead(localPath);
    }

    public DownloadInfo? GetDownloadStatus(string songId)
    {
        _activeDownloads.TryGetValue(songId, out var info);
        return info;
    }

    public async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrEmpty(_arl))
        {
            _logger.LogWarning("Deezer ARL not configured");
            return false;
        }

        try
        {
            await InitializeAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deezer service not available");
            return false;
        }
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
            var request = new HttpRequestMessage(HttpMethod.Post, 
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
                
                _logger.LogInformation("Deezer token refreshed: {Token}...", _apiToken?.Substring(0, Math.Min(16, _apiToken?.Length ?? 0)));
                return true;
            }

            throw new Exception("Invalid ARL token");
        });
    }

    private async Task<DownloadResult> GetTrackDownloadInfoAsync(string trackId, CancellationToken cancellationToken)
    {
        var tryDownload = async (string arl) =>
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
                
                if (!trackDoc.RootElement.TryGetProperty("track_token", out var trackTokenElement))
                {
                    throw new Exception("Track not found or track_token missing");
                }

                var trackToken = trackTokenElement.GetString();
                var title = trackDoc.RootElement.GetProperty("title").GetString() ?? "";
                var artist = trackDoc.RootElement.TryGetProperty("artist", out var artistEl) 
                    ? artistEl.GetProperty("name").GetString() ?? "" 
                    : "";

                // Get download URL via media API
                var mediaRequest = new
                {
                    license_token = _licenseToken,
                    media = new[]
                    {
                        new
                        {
                            type = "FULL",
                            formats = new[]
                            {
                                new { cipher = "BF_CBC_STRIPE", format = "MP3_128" },
                                new { cipher = "BF_CBC_STRIPE", format = "MP3_320" },
                                new { cipher = "BF_CBC_STRIPE", format = "FLAC" }
                            }
                        }
                    },
                    track_tokens = new[] { trackToken }
                };

                var mediaHttpRequest = new HttpRequestMessage(HttpMethod.Post, "https://media.deezer.com/v1/get_url");
                mediaHttpRequest.Content = new StringContent(
                    JsonSerializer.Serialize(mediaRequest), 
                    Encoding.UTF8, 
                    "application/json");

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
                if (!firstData.TryGetProperty("media", out var media) || 
                    media.GetArrayLength() == 0)
                {
                    throw new Exception("No media sources available - track may be unavailable in your region");
                }

                string? downloadUrl = null;
                string? format = null;

                foreach (var mediaItem in media.EnumerateArray())
                {
                    if (mediaItem.TryGetProperty("sources", out var sources) && 
                        sources.GetArrayLength() > 0)
                    {
                        downloadUrl = sources[0].GetProperty("url").GetString();
                        format = mediaItem.GetProperty("format").GetString();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    throw new Exception("No download URL found in media sources - track may be region locked");
                }

                return new DownloadResult
                {
                    DownloadUrl = downloadUrl,
                    Format = format ?? "MP3_128",
                    Title = title,
                    Artist = artist
                };
            });
        };

        try
        {
            return await tryDownload(_arl!);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(_arlFallback))
            {
                _logger.LogWarning(ex, "Primary ARL failed, trying fallback ARL...");
                _usingFallback = true;
                return await tryDownload(_arlFallback);
            }
            throw;
        }
    }

    private async Task<string> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var downloadInfo = await GetTrackDownloadInfoAsync(trackId, cancellationToken);
        
        _logger.LogInformation("Track token obtained for: {Title} - {Artist}", downloadInfo.Title, downloadInfo.Artist);
        _logger.LogInformation("Using format: {Format}", downloadInfo.Format);

        // Déterminer l'extension basée sur le format
        var extension = downloadInfo.Format?.ToUpper() switch
        {
            "FLAC" => ".flac",
            _ => ".mp3"
        };

        // Générer le nom de fichier
        var safeTitle = SanitizeFileName(song.Title);
        var safeArtist = SanitizeFileName(song.Artist);
        var fileName = $"{safeArtist} - {safeTitle}{extension}";
        var outputPath = Path.Combine(_downloadPath, fileName);

        // Éviter les conflits
        var counter = 1;
        while (File.Exists(outputPath))
        {
            fileName = $"{safeArtist} - {safeTitle} ({counter}){extension}";
            outputPath = Path.Combine(_downloadPath, fileName);
            counter++;
        }

        // Télécharger le fichier chiffré
        var response = await RetryWithBackoffAsync(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DownloadUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
            request.Headers.Add("Accept", "*/*");
            
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        });

        response.EnsureSuccessStatusCode();

        // Télécharger et déchiffrer
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputFile = File.Create(outputPath);
        
        await DecryptAndWriteStreamAsync(responseStream, outputFile, trackId, cancellationToken);

        return outputPath;
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

            // Chaque 3ème chunk (index % 3 == 0) est chiffré
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
        // Note: .NET ne supporte pas nativement Blowfish
        // On utilise BouncyCastle ou une implémentation custom
        // Pour l'instant, on utilise un appel à OpenSSL via Process (comme le JS)
        
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "openssl";
        process.StartInfo.Arguments = $"enc -d -bf-cbc -K {Convert.ToHexString(key).ToLower()} -iv {Convert.ToHexString(iv).ToLower()} -nopad -provider legacy -provider default";
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        
        using var stdin = process.StandardInput.BaseStream;
        stdin.Write(data, 0, data.Length);
        stdin.Close();

        using var stdout = process.StandardOutput.BaseStream;
        using var ms = new MemoryStream();
        stdout.CopyTo(ms);
        
        process.WaitForExit();
        
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new Exception($"OpenSSL decryption failed: {error}");
        }

        return ms.ToArray();
    }

    #endregion

    #region Utility Methods

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
                    _logger.LogWarning("Retry attempt {Attempt}/{MaxRetries} after {Delay}ms ({Message})", 
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

    private string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }
        
        return sanitized.Trim();
    }

    #endregion

    private class DownloadResult
    {
        public string DownloadUrl { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
    }
}
