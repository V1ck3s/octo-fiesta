using System.Buffers.Binary;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Patches the movie/track/media duration fields of a fragmented MP4 (fMP4) in place.
///
/// Tidal HI_RES_LOSSLESS is served as DASH fMP4 (FLAC-in-MP4): the assembled file's
/// <c>moov/mvhd</c>, <c>trak/tkhd</c> and <c>mdia/mdhd</c> duration fields are all 0 — the
/// real timing lives in the per-fragment <c>moof</c> boxes. Smart demuxers (ffmpeg) sum the
/// fragments, but tag-based scanners (TagLibSharp, and Navidrome's scan) read <c>mvhd</c> and
/// therefore report a 0:00 length. We patch those fixed-size duration fields with the known
/// total duration so the file is self-describing — no transcoding involved, no size change.
/// </summary>
public static class Mp4DurationPatcher
{
    /// <summary>
    /// Patches the duration fields of the fMP4 at <paramref name="filePath"/> in place.
    /// Returns true if at least one duration field was written.
    /// </summary>
    public static bool PatchDuration(string filePath, double durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return false;
        }

        var buffer = File.ReadAllBytes(filePath);
        if (!PatchDuration(buffer, durationSeconds))
        {
            return false;
        }

        File.WriteAllBytes(filePath, buffer);
        return true;
    }

    /// <summary>
    /// Patches the duration fields inside <paramref name="buffer"/> in place.
    /// Only fixed-size fields are overwritten, so the buffer length never changes.
    /// </summary>
    public static bool PatchDuration(byte[] buffer, double durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return false;
        }

        if (!TryFindBox(buffer, 0, buffer.Length, "moov", out var moov))
        {
            return false;
        }

        var patched = false;

        // mvhd carries the movie timescale; tkhd durations are expressed in it.
        uint movieTimescale = 0;
        if (TryFindBox(buffer, moov.ContentStart, moov.End, "mvhd", out var mvhd))
        {
            movieTimescale = ReadVersionedTimescale(buffer, mvhd);
            patched |= PatchVersionedDuration(buffer, mvhd, durationSeconds);
        }

        for (var trak = moov.ContentStart; TryFindBox(buffer, trak, moov.End, "trak", out var trakBox); trak = trakBox.End)
        {
            if (movieTimescale > 0 && TryFindBox(buffer, trakBox.ContentStart, trakBox.End, "tkhd", out var tkhd))
            {
                patched |= PatchTkhdDuration(buffer, tkhd, durationSeconds, movieTimescale);
            }

            if (TryFindBox(buffer, trakBox.ContentStart, trakBox.End, "mdia", out var mdia)
                && TryFindBox(buffer, mdia.ContentStart, mdia.End, "mdhd", out var mdhd))
            {
                // mdhd uses the media (sample-rate) timescale, read from the box itself.
                patched |= PatchVersionedDuration(buffer, mdhd, durationSeconds);
            }
        }

        return patched;
    }

    private readonly record struct Box(int Offset, int HeaderSize, int Size)
    {
        public int ContentStart => Offset + HeaderSize;
        public int End => Offset + Size;
    }

    /// <summary>Finds the first child box of <paramref name="type"/> in [start, end).</summary>
    private static bool TryFindBox(byte[] buffer, int start, int end, string type, out Box box)
    {
        box = default;
        var offset = start;
        while (offset + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
            var boxType = System.Text.Encoding.ASCII.GetString(buffer, offset + 4, 4);
            var headerSize = 8;

            if (size == 1)
            {
                if (offset + 16 > end)
                {
                    return false;
                }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset + 8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - offset; // box extends to the end of the region
            }

            if (size < headerSize || offset + size > end)
            {
                return false; // malformed — stop walking
            }

            if (boxType == type)
            {
                box = new Box(offset, headerSize, (int)size);
                return true;
            }

            offset += (int)size;
        }

        return false;
    }

    // mvhd/mdhd full-box layout after [version(1) flags(3)]:
    //   v0: creation(4) modification(4) timescale(4) duration(4)
    //   v1: creation(8) modification(8) timescale(4) duration(8)
    private static uint ReadVersionedTimescale(byte[] buffer, Box box)
    {
        var content = box.ContentStart;
        var version = buffer[content];
        var timescaleOffset = content + (version == 1 ? 20 : 12);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(timescaleOffset, 4));
    }

    private static bool PatchVersionedDuration(byte[] buffer, Box box, double durationSeconds)
    {
        var content = box.ContentStart;
        var version = buffer[content];

        if (version == 1)
        {
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(content + 20, 4));
            if (timescale == 0)
            {
                return false;
            }
            BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(content + 24, 8), (ulong)Math.Round(durationSeconds * timescale));
        }
        else
        {
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(content + 12, 4));
            if (timescale == 0)
            {
                return false;
            }
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(content + 16, 4), (uint)Math.Round(durationSeconds * timescale));
        }

        return true;
    }

    // tkhd full-box layout after [version(1) flags(3)]:
    //   v0: creation(4) modification(4) track_id(4) reserved(4) duration(4)
    //   v1: creation(8) modification(8) track_id(4) reserved(4) duration(8)
    private static bool PatchTkhdDuration(byte[] buffer, Box box, double durationSeconds, uint movieTimescale)
    {
        var content = box.ContentStart;
        var version = buffer[content];

        if (version == 1)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(content + 28, 8), (ulong)Math.Round(durationSeconds * movieTimescale));
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(content + 20, 4), (uint)Math.Round(durationSeconds * movieTimescale));
        }

        return true;
    }
}
