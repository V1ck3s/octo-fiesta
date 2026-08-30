namespace octo_fiesta.Services.Common;

/// <summary>
/// Read-only forward Stream that concatenates the bodies of multiple HTTP GETs.
/// Used for DASH downloads: init segment + N media segments must be reassembled in order.
/// </summary>
internal sealed class MultiSegmentHttpStream : Stream
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _urls;
    private int _index = -1;
    private HttpResponseMessage? _currentResponse;
    private Stream? _currentStream;

    public MultiSegmentHttpStream(HttpClient http, IReadOnlyList<string> urls)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _urls = urls ?? throw new ArgumentNullException(nameof(urls));
        if (_urls.Count == 0)
        {
            throw new ArgumentException("At least one segment URL is required", nameof(urls));
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_currentStream == null)
            {
                if (!await AdvanceAsync(cancellationToken).ConfigureAwait(false)) return 0;
            }

            var read = await _currentStream!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) return read;
            await DisposeCurrentAsync().ConfigureAwait(false);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task<bool> AdvanceAsync(CancellationToken cancellationToken)
    {
        _index++;
        if (_index >= _urls.Count) return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, _urls[_index]);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.Accept.ParseAdd("*/*");

        _currentResponse = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        _currentResponse.EnsureSuccessStatusCode();
        _currentStream = await _currentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task DisposeCurrentAsync()
    {
        if (_currentStream != null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }
        _currentResponse?.Dispose();
        _currentResponse = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _currentStream?.Dispose();
            _currentResponse?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeCurrentAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
