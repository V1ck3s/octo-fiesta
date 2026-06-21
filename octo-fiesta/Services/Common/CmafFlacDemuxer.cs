using System.Buffers.Binary;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Extracts raw FLAC audio from a decrypted CMAF/MP4 container.
/// Amazon Music delivers FLAC as FLAC-in-CMAF; after CENC decryption each mdat box
/// contains consecutive raw FLAC frames. The STREAMINFO is stored in moov → stsd → fLaC → dfLa.
/// This class reconstructs a valid .flac file: fLaC marker + STREAMINFO block + audio frames.
/// </summary>
internal static class CmafFlacDemuxer
{
    private static readonly byte[] FlacMarker = "fLaC"u8.ToArray();

    /// <summary>
    /// Tries to extract raw FLAC from <paramref name="inputPath"/> (a decrypted CMAF/MP4 file).
    /// Returns true and writes a valid .flac file to <paramref name="outputPath"/>.
    /// Returns false if the content does not appear to be FLAC (caller keeps the .m4a as-is).
    /// </summary>
    public static bool TryDemux(string inputPath, string outputPath)
    {
        var data = File.ReadAllBytes(inputPath);

        var mdatPayloads = CollectMdatPayloads(data);
        if (mdatPayloads.Count == 0) return false;

        var (firstStart, firstLen) = mdatPayloads[0];
        if (firstLen < 2 || !IsFlacSync(data, firstStart)) return false;

        // Find STREAMINFO from the dfLa box in the moov tree.
        var streamInfo = FindStreamInfo(data);

        using var output = File.Create(outputPath);

        // Without STREAMINFO the file would be invalid — keep .m4a as fallback.
        if (streamInfo is not { Length: 38 }) return false;

        // Write FLAC stream marker.
        output.Write(FlacMarker);

        // dfLa gives the complete METADATA_BLOCK_STREAMINFO (4-byte block header + 34-byte data).
        // Set is_last bit so decoders know this is the only metadata block.
        streamInfo[0] |= 0x80;
        output.Write(streamInfo);

        foreach (var (start, length) in mdatPayloads)
            output.Write(data, start, length);

        return true;
    }

    // FLAC frame sync code: top 14 bits = 0x3FFE → bytes 0xFF 0xF8 or 0xFF 0xF9.
    private static bool IsFlacSync(byte[] data, int offset) =>
        offset + 1 < data.Length && data[offset] == 0xFF && (data[offset + 1] & 0xFE) == 0xF8;

    // Walk the moov box tree to find dfLa and return its 34-byte STREAMINFO payload.
    // Path: moov → trak → mdia → minf → stbl → stsd → fLaC → dfLa
    private static byte[]? FindStreamInfo(byte[] data)
    {
        int pos = 0;
        while (pos <= data.Length - 8)
        {
            long boxSize = ReadBoxSize(data, pos, out int headerLen);
            if (boxSize < 8 || pos + boxSize > data.Length) break;

            if (ReadBoxType(data, pos) == "moov")
                return SearchDfLa(data, pos + headerLen, pos + (int)boxSize);

            pos += (int)boxSize;
        }
        return null;
    }

    private static byte[]? SearchDfLa(byte[] data, int start, int end)
    {
        int pos = start;
        while (pos <= end - 8)
        {
            long sz = ReadBoxSize(data, pos, out int hl);
            if (sz < 8 || pos + sz > end) break;

            string type = ReadBoxType(data, pos);
            int body = pos + hl;

            if (type == "dfLa")
            {
                // FullBox: 4 bytes (version + flags), then the FLAC METADATA_BLOCK_STREAMINFO
                // which is: 4-byte block header + 34-byte STREAMINFO data = 38 bytes total.
                int dataStart = body + 4;
                int dataLen = (int)sz - hl - 4;
                if (dataLen >= 38)
                    return data[dataStart..(dataStart + 38)];
                return null;
            }

            int childStart;
            if (type == "stsd")
                // FullBox: skip version(1)+flags(3)+entry_count(4) = 8 bytes before sample entries.
                childStart = body + 8;
            else if (type is "fLaC" or "enca")
                // AudioSampleEntry: 28-byte header (reserved+data_ref+reserved+channels+samplesize+
                // pre_defined+reserved+samplerate) before codec-specific children.
                childStart = body + 28;
            else if (IsSimpleContainerBox(type))
                childStart = body;
            else
            {
                pos += (int)sz;
                continue;
            }

            var found = SearchDfLa(data, childStart, pos + (int)sz);
            if (found != null) return found;

            pos += (int)sz;
        }
        return null;
    }

    private static bool IsSimpleContainerBox(string t) => t is
        "trak" or "mdia" or "minf" or "stbl" or "sinf" or "schi";

    private static List<(int Start, int Length)> CollectMdatPayloads(byte[] data)
    {
        var result = new List<(int, int)>();
        int pos = 0;
        while (pos <= data.Length - 8)
        {
            long boxSize = ReadBoxSize(data, pos, out int headerLen);
            if (boxSize < 8 || pos + boxSize > data.Length) break;

            if (ReadBoxType(data, pos) == "mdat")
            {
                int payloadStart = pos + headerLen;
                int payloadLen = (int)(boxSize - headerLen);
                if (payloadLen > 0)
                    result.Add((payloadStart, payloadLen));
            }

            pos += (int)boxSize;
        }
        return result;
    }

    private static long ReadBoxSize(byte[] data, int pos, out int headerLen)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
        if (size == 1)
        {
            headerLen = 16;
            return (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8, 8));
        }
        headerLen = 8;
        return size;
    }

    private static string ReadBoxType(byte[] data, int pos) =>
        System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
}
