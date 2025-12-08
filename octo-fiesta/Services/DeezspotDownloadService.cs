using octo_fiesta.Models;
using System.Diagnostics;

namespace octo_fiesta.Services;

/// <summary>
/// Implémentation du service de téléchargement utilisant Deezspot (ou similaire)
/// Cette implémentation est un placeholder - à adapter selon l'outil de téléchargement choisi
/// </summary>
public class DeezspotDownloadService : IDownloadService
{
    private readonly IConfiguration _configuration;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IMusicMetadataService _metadataService;
    private readonly ILogger<DeezspotDownloadService> _logger;
    private readonly Dictionary<string, DownloadInfo> _activeDownloads = new();
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly string _downloadPath;
    private readonly string? _deezspotPath;

    public DeezspotDownloadService(
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        ILogger<DeezspotDownloadService> logger)
    {
        _configuration = configuration;
        _localLibraryService = localLibraryService;
        _metadataService = metadataService;
        _logger = logger;
        _downloadPath = configuration["Library:DownloadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        _deezspotPath = configuration["Deezspot:ExecutablePath"];
        
        if (!Directory.Exists(_downloadPath))
        {
            Directory.CreateDirectory(_downloadPath);
        }
    }

    public async Task<string> DownloadSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        var songId = $"ext-{externalProvider}-{externalId}";
        
        // Vérifier si déjà téléchargé
        var existingPath = await _localLibraryService.GetLocalPathForExternalSongAsync(externalProvider, externalId);
        if (existingPath != null && File.Exists(existingPath))
        {
            return existingPath;
        }

        // Vérifier si téléchargement en cours
        if (_activeDownloads.TryGetValue(songId, out var activeDownload) && activeDownload.Status == DownloadStatus.InProgress)
        {
            // Attendre la fin du téléchargement en cours
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
            // Récupérer les métadonnées pour le nom de fichier
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
                var localPath = await ExecuteDownloadAsync(externalProvider, externalId, song, cancellationToken);
                
                downloadInfo.Status = DownloadStatus.Completed;
                downloadInfo.LocalPath = localPath;
                downloadInfo.CompletedAt = DateTime.UtcNow;
                
                // Enregistrer dans la bibliothèque locale
                song.LocalPath = localPath;
                await _localLibraryService.RegisterDownloadedSongAsync(song, localPath);
                
                return localPath;
            }
            catch (Exception ex)
            {
                downloadInfo.Status = DownloadStatus.Failed;
                downloadInfo.ErrorMessage = ex.Message;
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
        // Pour le streaming à la volée, on télécharge d'abord le fichier puis on le stream
        // Une implémentation plus avancée pourrait utiliser des pipes pour streamer pendant le téléchargement
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
        if (string.IsNullOrEmpty(_deezspotPath))
        {
            _logger.LogWarning("Deezspot path not configured");
            return false;
        }

        if (!File.Exists(_deezspotPath))
        {
            _logger.LogWarning("Deezspot executable not found at {Path}", _deezspotPath);
            return false;
        }

        await Task.CompletedTask;
        return true;
    }

    private async Task<string> ExecuteDownloadAsync(string provider, string externalId, Song song, CancellationToken cancellationToken)
    {
        // Générer un nom de fichier sécurisé
        var safeTitle = SanitizeFileName(song.Title);
        var safeArtist = SanitizeFileName(song.Artist);
        var fileName = $"{safeArtist} - {safeTitle}.mp3";
        var outputPath = Path.Combine(_downloadPath, fileName);

        // Éviter les conflits de noms
        var counter = 1;
        while (File.Exists(outputPath))
        {
            fileName = $"{safeArtist} - {safeTitle} ({counter}).mp3";
            outputPath = Path.Combine(_downloadPath, fileName);
            counter++;
        }

        if (string.IsNullOrEmpty(_deezspotPath))
        {
            throw new InvalidOperationException("Deezspot executable path not configured. Set 'Deezspot:ExecutablePath' in configuration.");
        }

        // Construire la commande Deezspot
        // Note: La syntaxe exacte dépend de la version de Deezspot utilisée
        var trackUrl = provider == "deezer" 
            ? $"https://www.deezer.com/track/{externalId}"
            : $"https://open.spotify.com/track/{externalId}";

        var processInfo = new ProcessStartInfo
        {
            FileName = _deezspotPath,
            Arguments = $"download \"{trackUrl}\" -o \"{_downloadPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogInformation("Starting download: {Command} {Args}", processInfo.FileName, processInfo.Arguments);

        using var process = new Process { StartInfo = processInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("Download failed: {Error}", error);
            throw new Exception($"Download failed: {error}");
        }

        // Chercher le fichier téléchargé (Deezspot peut utiliser son propre nommage)
        var downloadedFiles = Directory.GetFiles(_downloadPath, "*.mp3")
            .OrderByDescending(f => File.GetCreationTime(f))
            .ToList();

        if (downloadedFiles.Any())
        {
            var latestFile = downloadedFiles.First();
            
            // Si le fichier a un nom différent, on peut le renommer
            if (latestFile != outputPath && File.GetCreationTime(latestFile) > DateTime.UtcNow.AddMinutes(-5))
            {
                _logger.LogInformation("Downloaded file: {File}", latestFile);
                return latestFile;
            }
        }

        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        throw new Exception("Download completed but file not found");
    }

    private string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        // Limiter la longueur
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }
        
        return sanitized.Trim();
    }
}
