namespace octo_fiesta.Services.Common;

/// <summary>
/// HttpResponseMessage Stream wrapper.
/// Reads content of HtttpResponseMessage as Stream.
/// When disposed itself, also disposes wrapped HttpResponseMessage.
/// </summary>
public class HttpResponseStream : Stream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _responseStream;

    private HttpResponseStream(HttpResponseMessage response, Stream responseStream)
    {
        _response = response;
        _responseStream = responseStream;
    }

    /// <summary>
    /// Non-blocking factory.
    /// </summary>
    /// <param name="response">HttpResponseMessage to wrap.</param>
    /// <returns></returns>
    public static async Task<HttpResponseStream> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        return new(response, await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    protected override void Dispose(bool disposing)
    {
        _responseStream.Dispose();
        _response.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _responseStream.DisposeAsync();
        _response.Dispose();
        await base.DisposeAsync();
        
        GC.SuppressFinalize(this);
    }
    
    public override bool CanRead => _responseStream.CanRead;

    public override bool CanSeek => _responseStream.CanSeek;

    public override bool CanWrite => _responseStream.CanWrite;

    public override long Length => _responseStream.Length;

    public override long Position { get => _responseStream.Position; set => _responseStream.Position = value; }

    public override void Flush() => _responseStream.Flush();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _responseStream.ReadAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => _responseStream.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => _responseStream.Seek(offset, origin);

    public override void SetLength(long value) => _responseStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _responseStream.Write(buffer, offset, count);
}