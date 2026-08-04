using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using IOFile = System.IO.File;

namespace octo_fiesta.Services.YouTube;

public class YouTubeDownloadService : BaseDownloadService
{
    private const string Provider = "youtube";
    private const string AlbumPrefix = "ext-youtube-album-";
    private readonly YouTubeSettings _settings;
    private readonly IYtDlpProcessRunner _processRunner;
    private readonly ILogger<YouTubeDownloadService> _logger;

    public YouTubeDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<YouTubeSettings> youtubeSettings,
        IYtDlpProcessRunner processRunner,
        IServiceProvider serviceProvider,
        ILogger<YouTubeDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {
        _settings = youtubeSettings.Value;
        _processRunner = processRunner;
        _logger = logger;
    }

    protected override string ProviderName => Provider;

    public override async Task<bool> IsAvailableAsync()
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("YouTube provider is disabled by configuration.");
            return false;
        }

        var result = await _processRunner.ExecuteAsync(_settings.YtDlpPath, ["--version"], CancellationToken.None);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("yt-dlp is not available at '{Path}'. stderr: {Error}", _settings.YtDlpPath, result.StandardError);
            return false;
        }

        return true;
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "octo-fiesta-youtube");
        Directory.CreateDirectory(tempRoot);

        var outputTemplate = Path.Combine(tempRoot, $"{trackId}.%(ext)s");
        var preferredFormat = string.IsNullOrWhiteSpace(_settings.AudioFormat) ? "m4a" : _settings.AudioFormat.Trim().ToLowerInvariant();
        var formatExpression = $"bestaudio[ext={preferredFormat}]/bestaudio";

        var args = new List<string>
        {
            "--no-playlist",
            "--no-warnings",
            "--restrict-filenames",
            "--format",
            formatExpression,
            "--output",
            outputTemplate,
            $"https://www.youtube.com/watch?v={trackId}"
        };

        if (!string.IsNullOrWhiteSpace(_settings.CookiesPath))
        {
            args.Add("--cookies");
            args.Add(_settings.CookiesPath!);
        }

        _logger.LogInformation("YouTube download started: trackId={TrackId}, title='{Title}'", trackId, song.Title);
        var result = await _processRunner.ExecuteAsync(_settings.YtDlpPath, args, cancellationToken);
        if (result.ExitCode != 0)
        {
            _logger.LogError("YouTube download failed: trackId={TrackId}, stderr={Error}", trackId, result.StandardError);
            throw new InvalidOperationException($"yt-dlp failed for track {trackId}: {result.StandardError}");
        }

        var downloadedFilePath = FindDownloadedFilePath(tempRoot, trackId);
        if (downloadedFilePath == null)
        {
            throw new FileNotFoundException($"yt-dlp did not create a file for track {trackId}");
        }

        var extension = Path.GetExtension(downloadedFilePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = $".{preferredFormat}";
        }

        _logger.LogInformation("YouTube download completed: trackId={TrackId}, file={FilePath}", trackId, downloadedFilePath);
        return new DownloadResult(
            new DeleteOnDisposeFileStream(downloadedFilePath),
            extension,
            extension.TrimStart('.').ToUpperInvariant());
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        if (albumId.StartsWith(AlbumPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return albumId[AlbumPrefix.Length..];
        }

        return null;
    }

    protected override string? GetTargetQuality() => _settings.AudioFormat?.ToUpperInvariant();

    private static string? FindDownloadedFilePath(string directory, string trackId)
    {
        return Directory.GetFiles(directory, $"{trackId}.*")
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private sealed class DeleteOnDisposeFileStream : Stream
    {
        private readonly string _path;
        private readonly FileStream _innerStream;
        private bool _disposed;

        public DeleteOnDisposeFileStream(string path)
        {
            _path = path;
            _innerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _innerStream.Length;
        public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }
        public override void Flush() => _innerStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(Span<byte> buffer) => _innerStream.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _innerStream.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _innerStream.ReadAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _innerStream.Dispose();
                try
                {
                    if (IOFile.Exists(_path))
                    {
                        IOFile.Delete(_path);
                    }
                }
                catch
                {
                }
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
