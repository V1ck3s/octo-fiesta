using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace octo_fiesta.Services.Deezer;

public sealed class DeezerDecryptedStream(Stream source, string trackId) : Stream
{
        
    // Deezer's standard Blowfish CBC encryption key for track decryption
    // This is a well-known constant used by the Deezer API, not a user-specific secret
    private const string BfSecret = "g4el58wc0zvf9na1";
    private static byte[] Iv { get => [0, 1, 2, 3, 4, 5, 6, 7]; }
    private readonly Stream _source = source;
    private readonly byte[] _bfKey = GetBlowfishKey(trackId);
    private readonly byte[] _readBuffer = new byte[2048];
    private byte[] _currentChunk = [];
    private int _chunkOffset = 0;
    private int _chunkIndex = 0;

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        // read and optionally decrypt new chunk from input stream
        if (_chunkOffset >= _currentChunk.Length)
        {
            var bytesRead = await _source.ReadAtLeastAsync(
                _readBuffer,
                _readBuffer.Length,
                throwOnEndOfStream: false,
                cancellationToken);
            
            if (bytesRead == 0) return 0;

            _currentChunk = _readBuffer.AsSpan(0, bytesRead).ToArray();

            // Every 3rd chunk (index % 3 == 0) is encrypted
            if (_chunkIndex % 3 == 0 && bytesRead == 2048)
            {
                _currentChunk = DecryptBlowfishCbc(_currentChunk);
            }

            _chunkOffset = 0;
            _chunkIndex++;
        }

        // copy (part of) current chunk into destination buffer
        int bytesToCopy = Math.Min(destination.Length, _currentChunk.Length - _chunkOffset);
        _currentChunk.AsSpan(_chunkOffset, bytesToCopy).CopyTo(destination.Span);

        _chunkOffset += bytesToCopy;

        return bytesToCopy;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _source.Dispose();
        base.Dispose(disposing);
    }
    
    public override async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await base.DisposeAsync();
    }

    private static byte[] GetBlowfishKey(string trackId)
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

    private byte[] DecryptBlowfishCbc(byte[] data)
    {
        // Use BouncyCastle for native Blowfish CBC decryption
        var engine = new BlowfishEngine();
        var cipher = new CbcBlockCipher(engine);
        cipher.Init(false, new ParametersWithIV(new KeyParameter(_bfKey), Iv));
        
        var output = new byte[data.Length];
        var blockSize = cipher.GetBlockSize(); // 8 bytes for Blowfish
        
        for (int offset = 0; offset < data.Length; offset += blockSize)
        {
            cipher.ProcessBlock(data, offset, output, offset);
        }
        
        return output;
    }
}