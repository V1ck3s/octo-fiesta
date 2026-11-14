namespace octo_fiesta.Services.Common;

/// <summary>
/// A track download that can be read while it is still being written. The provider stream
/// is copied into a scratch file and readers follow that file as it grows, so playback
/// starts with the first bytes instead of after the whole download and tagging.
/// </summary>
public sealed class ProgressiveDownload
{
    private readonly object _sync = new();
    private TaskCompletionSource _progress = NewSignal();
    private long _bytesWritten;
    private bool _completed;
    private Exception? _error;

    // Assumed when the duration is unknown: CD-quality FLAC at its uncompressed ceiling
    private const double FallbackBytesPerSecond = 1411 * 1000 / 8.0;

    public ProgressiveDownload(string partPath, long length, string targetPath, double? durationSeconds = null)
    {
        PartPath = partPath;
        Length = length;
        TargetPath = targetPath;
        DurationSeconds = durationSeconds is > 0 ? durationSeconds : null;
    }

    /// <summary>Playing time of the track, when known.</summary>
    public double? DurationSeconds { get; }

    /// <summary>Scratch file receiving the raw provider bytes (never tagged in place).</summary>
    public string PartPath { get; }

    /// <summary>Final size announced by the provider.</summary>
    public long Length { get; }

    /// <summary>Where the finished track will land; its extension gives the content type.</summary>
    public string TargetPath { get; }

    public long BytesWritten
    {
        get { lock (_sync) return _bytesWritten; }
    }

    public void ReportProgress(long totalBytes)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            _bytesWritten = totalBytes;
            signal = _progress;
            _progress = NewSignal();
        }
        signal.TrySetResult();
    }

    public void Complete() => Finish(null);

    public void Fail(Exception error) => Finish(error);

    /// <summary>
    /// Waits until bytes past <paramref name="position"/> are on disk or the download ended.
    /// Throws if the download failed.
    /// </summary>
    public async Task WaitForDataAsync(long position, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_error != null)
                {
                    throw new IOException("Track download failed while streaming", _error);
                }
                if (_bytesWritten > position || _completed)
                {
                    return;
                }
                wait = _progress.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Waits until <paramref name="seconds"/> of audio are on disk, or the download ended.
    /// The byte count comes from the track's average rate (size over duration).
    /// Throws if the download failed.
    /// </summary>
    public Task WaitForSecondsAsync(double seconds, CancellationToken cancellationToken)
    {
        if (seconds <= 0)
        {
            return Task.CompletedTask;
        }

        var bytesPerSecond = DurationSeconds is { } duration ? Length / duration : FallbackBytesPerSecond;
        var bytes = (long)Math.Min(Length, Math.Ceiling(bytesPerSecond * seconds));
        return bytes <= 0 ? Task.CompletedTask : WaitForDataAsync(bytes - 1, cancellationToken);
    }

    /// <summary>
    /// Opens a reader on the scratch file, or returns null once that file is gone.
    /// </summary>
    public Stream? TryOpenReader()
    {
        try
        {
            // bufferSize 0: no read-ahead, so a reader never caches the end of a file still growing
            var file = new FileStream(PartPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, useAsync: true);
            return new ProgressiveDownloadStream(this, file);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private void Finish(Exception? error)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_completed || _error != null)
            {
                return;
            }
            _completed = error == null;
            _error = error;
            signal = _progress;
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Read-only view of a <see cref="ProgressiveDownload"/>. Reports the announced length so
/// range requests work; a read past the downloaded part waits for the bytes to arrive.
/// </summary>
internal sealed class ProgressiveDownloadStream : Stream
{
    private readonly ProgressiveDownload _download;
    private readonly FileStream _file;
    private long _position;

    public ProgressiveDownloadStream(ProgressiveDownload download, FileStream file)
    {
        _download = download;
        _file = file;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _download.Length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || _position >= Length)
        {
            return 0;
        }

        await _download.WaitForDataAsync(_position, cancellationToken);

        var available = Math.Min(_download.BytesWritten, Length) - _position;
        if (available <= 0)
        {
            // Download finished short of the announced length
            return 0;
        }

        _file.Position = _position;
        var read = await _file.ReadAsync(buffer[..(int)Math.Min(buffer.Length, available)], cancellationToken);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _file.DisposeAsync();
        await base.DisposeAsync();
    }
}
