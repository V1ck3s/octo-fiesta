using System.Buffers.Binary;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Decrypts CENC-encrypted CMAF/MP4 files in pure .NET using BouncyCastle AES-128-CTR.
/// Handles the ISO/IEC 23001-7 'cenc' scheme as used by Amazon Music via amz.squid.wtf.
/// No external binary dependencies — replaces the ffmpeg-based approach.
///
/// Box parsing covers: moov→trac→mdia→minf→stbl→stsd→enca→sinf→schi→tenc (IV size),
/// and per-fragment moof→traf→trun (sample sizes) + moof→traf→senc (per-sample IVs).
/// Clear (non-encrypted) fragments are passed through unchanged.
/// </summary>
internal static class CmafCencDecryptor
{
    public static void Decrypt(string inputPath, string outputPath, byte[] key)
    {
        var data = File.ReadAllBytes(inputPath);
        DecryptInPlace(data, key);
        File.WriteAllBytes(outputPath, data);
    }

    // ── Top-level walk ────────────────────────────────────────────────────────

    private static void DecryptInPlace(byte[] data, byte[] key)
    {
        int ivSize = FindDefaultIvSize(data);

        int pos = 0;
        while (pos <= data.Length - 8)
        {
            long boxSize = ReadBoxSize(data, pos, out int headerLen);
            if (boxSize <= 0 || pos + boxSize > data.Length) break;

            if (ReadBoxType(data, pos) == "moof")
            {
                ParseMoofThenDecryptMdat(data, pos, (int)boxSize, ivSize, key);
            }

            pos += (int)boxSize;
        }
    }

    // ── Default IV size from tenc ─────────────────────────────────────────────

    private static int FindDefaultIvSize(byte[] data)
    {
        int pos = 0;
        while (pos <= data.Length - 8)
        {
            long boxSize = ReadBoxSize(data, pos, out _);
            if (boxSize <= 0) break;
            if (ReadBoxType(data, pos) == "moov")
            {
                int sz = SearchTenc(data, pos + 8, pos + (int)boxSize);
                return sz > 0 ? sz : 8; // Amazon Music always uses 8-byte IVs
            }
            pos += (int)boxSize;
        }
        return 8;
    }

    private static int SearchTenc(byte[] data, int start, int end)
    {
        int pos = start;
        while (pos <= end - 8)
        {
            long sz = ReadBoxSize(data, pos, out int hl);
            if (sz <= 0 || pos + sz > end) break;
            string type = ReadBoxType(data, pos);
            int body = pos + hl; // first byte of box content

            if (type == "tenc")
            {
                // FullBox: version(1) flags(3)
                // v0: reserved(1) reserved(1) isProtected(1) Per_Sample_IV_Size(1) KID(16)
                // v1: crypt_skip_byte(1)      isProtected(1) Per_Sample_IV_Size(1) KID(16)
                if (body + 8 <= data.Length)
                {
                    int version = data[body];
                    int ivSzOffset = version == 0 ? body + 7 : body + 6;
                    if (ivSzOffset < data.Length)
                    {
                        int ivSz = data[ivSzOffset];
                        if (ivSz == 8 || ivSz == 16) return ivSz;
                    }
                }
            }
            else if (type == "stsd")
            {
                // FullBox: skip version(1)+flags(3)+entry_count(4) before children
                int found = SearchTenc(data, body + 8, pos + (int)sz);
                if (found > 0) return found;
            }
            else if (type is "enca" or "mp4a")
            {
                // AudioSampleEntry: skip reserved(6)+data_ref_index(2)+reserved(8)+
                //   channelcount(2)+samplesize(2)+pre_defined(2)+reserved(2)+samplerate(4) = 28 bytes
                int found = SearchTenc(data, body + 28, pos + (int)sz);
                if (found > 0) return found;
            }
            else if (IsSimpleContainer(type))
            {
                int found = SearchTenc(data, body, pos + (int)sz);
                if (found > 0) return found;
            }

            pos += (int)sz;
        }
        return 0;
    }

    private static bool IsSimpleContainer(string t) => t is
        "moov" or "trak" or "mdia" or "minf" or "stbl" or "sinf" or "schi" or "udta" or "edts";

    // ── moof / mdat decryption ────────────────────────────────────────────────

    private static void ParseMoofThenDecryptMdat(byte[] data, int moofStart, int moofSize, int ivSize, byte[] key)
    {
        int[] sampleSizes = [];
        (byte[] Iv, (int Clear, int Prot)[] Subs)[] sampleEnc = [];

        int pos = moofStart + 8;
        int end = moofStart + moofSize;
        while (pos <= end - 8)
        {
            long sz = ReadBoxSize(data, pos, out int hl);
            if (sz <= 0 || pos + sz > end) break;
            if (ReadBoxType(data, pos) == "traf")
                ParseTraf(data, pos + hl, pos + (int)sz, ivSize, ref sampleSizes, ref sampleEnc);
            pos += (int)sz;
        }

        // No encryption info → clear fragment, nothing to decrypt
        if (sampleEnc.Length == 0) return;

        int mdatStart = moofStart + moofSize;
        if (mdatStart > data.Length - 8 || ReadBoxType(data, mdatStart) != "mdat") return;

        ReadBoxSize(data, mdatStart, out int mdatHl);
        DecryptSamples(data, mdatStart + mdatHl, sampleSizes, sampleEnc, key);
    }

    private static void ParseTraf(byte[] data, int start, int end, int ivSize,
        ref int[] sampleSizes, ref (byte[] Iv, (int Clear, int Prot)[] Subs)[] sampleEnc)
    {
        int defaultSampleSize = 0;
        int pos = start;
        while (pos <= end - 8)
        {
            long sz = ReadBoxSize(data, pos, out int hl);
            if (sz <= 0 || pos + sz > end) break;
            string type = ReadBoxType(data, pos);
            int body = pos + hl;

            switch (type)
            {
                case "tfhd":
                    // FullBox: version(1)+flags(3) = 4 bytes, then track_ID(4)
                    uint tfhdFlags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body, 4)) & 0xFFFFFFu;
                    int p = body + 4 + 4; // skip version+flags, track_ID
                    if ((tfhdFlags & 0x1u) != 0) p += 8;  // base_data_offset
                    if ((tfhdFlags & 0x2u) != 0) p += 4;  // sample_description_index
                    if ((tfhdFlags & 0x8u) != 0) p += 4;  // default_sample_duration
                    if ((tfhdFlags & 0x10u) != 0)
                        defaultSampleSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                    break;

                case "trun":
                    sampleSizes = ParseTrun(data, body, defaultSampleSize);
                    break;

                case "senc":
                    sampleEnc = ParseSenc(data, body, ivSize);
                    break;
            }

            pos += (int)sz;
        }
    }

    private static int[] ParseTrun(byte[] data, int body, int defaultSampleSize)
    {
        // FullBox: version(1)+flags(3)+sample_count(4) then optional fields
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body, 4)) & 0xFFFFFFu;
        int sampleCount = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4, 4));
        int pos = body + 8;

        if ((flags & 0x1u) != 0) pos += 4;  // data_offset
        if ((flags & 0x4u) != 0) pos += 4;  // first_sample_flags

        bool hasDuration = (flags & 0x100u) != 0;
        bool hasSize     = (flags & 0x200u) != 0;
        bool hasFlags    = (flags & 0x400u) != 0;
        bool hasCto      = (flags & 0x800u) != 0;

        var sizes = new int[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            if (hasDuration) pos += 4;
            sizes[i] = hasSize
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
                : defaultSampleSize;
            if (hasSize) pos += 4;
            if (hasFlags) pos += 4;
            if (hasCto)  pos += 4;
        }
        return sizes;
    }

    private static (byte[] Iv, (int Clear, int Prot)[] Subs)[] ParseSenc(byte[] data, int body, int ivSize)
    {
        // FullBox: version(1)+flags(3)+sample_count(4)
        // flags bit 0x2 = UseSubSampleEncryption
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body, 4)) & 0xFFFFFFu;
        bool hasSubs = (flags & 0x2u) != 0;
        int sampleCount = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4, 4));
        int pos = body + 8;

        var result = new (byte[] Iv, (int Clear, int Prot)[] Subs)[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            var iv = new byte[ivSize];
            Array.Copy(data, pos, iv, 0, ivSize);
            pos += ivSize;

            (int, int)[] subs = [];
            if (hasSubs)
            {
                int subCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
                pos += 2;
                subs = new (int, int)[subCount];
                for (int j = 0; j < subCount; j++)
                {
                    int clear = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
                    int prot  = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 2, 4));
                    subs[j] = (clear, prot);
                    pos += 6;
                }
            }
            result[i] = (iv, subs);
        }
        return result;
    }

    // ── Sample-level AES-128-CTR decryption ──────────────────────────────────

    private static void DecryptSamples(byte[] data, int mdatDataStart,
        int[] sizes, (byte[] Iv, (int Clear, int Prot)[] Subs)[] encInfo, byte[] key)
    {
        int count = Math.Min(sizes.Length, encInfo.Length);
        int pos = mdatDataStart;

        for (int i = 0; i < count; i++)
        {
            int sampleSize = sizes[i];
            if (pos + sampleSize > data.Length) break;

            var (iv, subs) = encInfo[i];

            if (subs.Length == 0)
            {
                // Entire sample is encrypted
                AesCtrDecrypt(data, pos, sampleSize, key, iv);
            }
            else
            {
                // Partial encryption: clear bytes → skip, protected bytes → decrypt
                // Per CENC spec the IV is shared for the whole sample; AES-CTR counter
                // continues from where it left off across subsample boundaries.
                var cipher = MakeCipher(key, iv);
                int samplePos = pos;
                foreach (var (clear, prot) in subs)
                {
                    // Advance the cipher counter over the clear bytes without XOR-ing them
                    SkipCipherBlocks(cipher, clear);
                    samplePos += clear;
                    if (prot > 0)
                    {
                        AesCtrDecryptWithCipher(cipher, data, samplePos, prot);
                        samplePos += prot;
                    }
                }
            }

            pos += sampleSize;
        }
    }

    // Decrypt `length` bytes at `data[offset]` in-place using a fresh AES-CTR cipher.
    private static void AesCtrDecrypt(byte[] data, int offset, int length, byte[] key, byte[] iv)
    {
        var cipher = MakeCipher(key, iv);
        AesCtrDecryptWithCipher(cipher, data, offset, length);
    }

    // Decrypt `length` bytes using a cipher that may already be mid-stream (for subsamples).
    private static void AesCtrDecryptWithCipher(SicBlockCipher cipher, byte[] data, int offset, int length)
    {
        var buf = new byte[16];
        int end = offset + length;
        int pos = offset;
        while (pos < end)
        {
            int block = Math.Min(16, end - pos);
            Array.Clear(buf);
            Array.Copy(data, pos, buf, 0, block);
            cipher.ProcessBlock(buf, 0, buf, 0);
            Array.Copy(buf, 0, data, pos, block);
            pos += block;
        }
    }

    // Advance the AES-CTR counter over `byteCount` bytes without producing output.
    // Used to stay in sync with the keystream across clear subsample regions.
    private static void SkipCipherBlocks(SicBlockCipher cipher, int byteCount)
    {
        if (byteCount <= 0) return;
        var buf = new byte[16];
        var dummy = new byte[16];
        int remaining = byteCount;
        while (remaining > 0)
        {
            int block = Math.Min(16, remaining);
            Array.Clear(buf);
            cipher.ProcessBlock(buf, 0, dummy, 0); // advance internal counter
            remaining -= block;
        }
    }

    private static SicBlockCipher MakeCipher(byte[] key, byte[] iv)
    {
        // CENC AES-128-CTR: 8-byte IV → pad to 16 bytes (high 8 = IV, low 8 = 0x00...).
        // The counter then increments by 1 per 16-byte block per the CENC spec.
        var iv16 = new byte[16];
        Array.Copy(iv, 0, iv16, 0, iv.Length);
        var cipher = new SicBlockCipher(new AesEngine());
        cipher.Init(false, new ParametersWithIV(new KeyParameter(key), iv16));
        return cipher;
    }

    // ── ISOBMFF box header helpers ────────────────────────────────────────────

    private static long ReadBoxSize(byte[] data, int pos, out int headerLen)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
        if (size == 1)
        {
            headerLen = 16; // 4 (size) + 4 (type) + 8 (largesize)
            return (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8, 8));
        }
        headerLen = 8;
        return size;
    }

    private static string ReadBoxType(byte[] data, int pos) =>
        System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
}
