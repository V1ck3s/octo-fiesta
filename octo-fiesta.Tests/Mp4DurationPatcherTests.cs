using System.Buffers.Binary;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

/// <summary>
/// Covers <see cref="Mp4DurationPatcher"/>, the fix for #251: a fragmented MP4 (Tidal HI_RES
/// FLAC-in-MP4) assembled from DASH segments has mvhd/tkhd/mdhd duration = 0, so tag scanners
/// report a 0:00 length. These tests build a minimal moov with zeroed durations (reproducing the
/// bug), then assert the patcher writes the correct, timescale-aware values.
/// </summary>
public class Mp4DurationPatcherTests
{
    private const uint MovieTimescale = 1000;
    private const uint MediaTimescale = 44100; // FLAC sample rate

    [Fact]
    public void PatchDuration_ZeroDurationFmp4_WritesTimescaleAwareDurations()
    {
        var file = BuildFmp4(mvhdVersion: 0);

        // Before: reproduces the bug — every duration field is 0.
        Assert.Equal(0u, ReadMvhdDuration(file));
        Assert.Equal(0u, ReadTkhdDuration(file));
        Assert.Equal(0u, ReadMdhdDuration(file));

        var patched = Mp4DurationPatcher.PatchDuration(file, 227.339);

        // After: each box carries the duration converted to ITS OWN timescale.
        Assert.True(patched);
        Assert.Equal((uint)Math.Round(227.339 * MovieTimescale), ReadMvhdDuration(file));
        Assert.Equal((uint)Math.Round(227.339 * MovieTimescale), ReadTkhdDuration(file));
        Assert.Equal((uint)Math.Round(227.339 * MediaTimescale), ReadMdhdDuration(file));
    }

    [Fact]
    public void PatchDuration_NonPositiveDuration_IsNoOp()
    {
        var file = BuildFmp4(mvhdVersion: 0);
        Assert.False(Mp4DurationPatcher.PatchDuration(file, 0));
        Assert.Equal(0u, ReadMvhdDuration(file));
    }

    [Fact]
    public void PatchDuration_NoMoov_ReturnsFalse()
    {
        // ftyp only — nothing to patch.
        var bytes = Box("ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D });
        Assert.False(Mp4DurationPatcher.PatchDuration(bytes, 123));
    }

    [Fact]
    public void PatchDuration_RoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"octo-fmp4-{Guid.NewGuid():N}.m4a");
        try
        {
            File.WriteAllBytes(path, BuildFmp4(mvhdVersion: 0));
            Assert.True(Mp4DurationPatcher.PatchDuration(path, 100.0));

            var reread = File.ReadAllBytes(path);
            Assert.Equal((uint)(100.0 * MovieTimescale), ReadMvhdDuration(reread));
            Assert.Equal((uint)(100.0 * MediaTimescale), ReadMdhdDuration(reread));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- minimal fMP4 builder ----------------------------------------------------------------
    // Layout: ftyp + moov{ mvhd, trak{ tkhd, mdia{ mdhd } } } — enough for the patcher's walk.

    private static byte[] BuildFmp4(byte mvhdVersion)
    {
        var ftyp = Box("ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D });
        var mvhd = Mvhd(mvhdVersion, MovieTimescale, duration: 0);
        var tkhd = Tkhd(version: 0, duration: 0);
        var mdhd = Mvhd(version: 0, MediaTimescale, duration: 0, type: "mdhd");
        var mdia = Box("mdia", mdhd);
        var trak = Box("trak", Concat(tkhd, mdia));
        var moov = Box("moov", Concat(mvhd, trak));
        return Concat(ftyp, moov);
    }

    private static byte[] Mvhd(byte version, uint timescale, uint duration, string type = "mvhd")
    {
        var body = new List<byte> { version, 0, 0, 0 };
        if (version == 1)
        {
            body.AddRange(new byte[16]); // creation(8) + modification(8)
            body.AddRange(BE32(timescale));
            body.AddRange(BE64(duration));
        }
        else
        {
            body.AddRange(new byte[8]); // creation(4) + modification(4)
            body.AddRange(BE32(timescale));
            body.AddRange(BE32(duration));
        }
        return Box(type, body.ToArray());
    }

    private static byte[] Tkhd(byte version, uint duration)
    {
        var body = new List<byte> { version, 0, 0, 0 };
        body.AddRange(new byte[8]);   // creation + modification
        body.AddRange(BE32(1));       // track_id
        body.AddRange(BE32(0));       // reserved
        body.AddRange(BE32(duration));
        body.AddRange(new byte[60]);  // remainder of tkhd (layer, matrix, width/height…)
        return Box("tkhd", body.ToArray());
    }

    private static byte[] Box(string type, byte[] content)
    {
        var size = 8 + content.Length;
        var result = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        content.CopyTo(result, 8);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }

    private static byte[] BE32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] BE64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }

    // --- readers (locate each box and read its duration field) -------------------------------

    private static uint ReadMvhdDuration(byte[] f) => ReadVersionedDuration(f, "mvhd");
    private static uint ReadMdhdDuration(byte[] f) => ReadVersionedDuration(f, "mdhd");

    private static uint ReadVersionedDuration(byte[] f, string type)
    {
        var c = FindContent(f, type);
        var version = f[c];
        return version == 1
            ? (uint)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(c + 24, 8))
            : BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(c + 16, 4));
    }

    private static uint ReadTkhdDuration(byte[] f)
    {
        var c = FindContent(f, "tkhd");
        return BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(c + 20, 4));
    }

    // Naive scan for the box type; sufficient for the test's flat layout.
    private static int FindContent(byte[] f, string type)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(type);
        for (var i = 4; i + 4 <= f.Length; i++)
        {
            if (f[i] == needle[0] && f[i + 1] == needle[1] && f[i + 2] == needle[2] && f[i + 3] == needle[3])
            {
                return i + 4; // content starts right after the 4-byte type (8-byte header box)
            }
        }
        throw new Xunit.Sdk.XunitException($"box {type} not found");
    }
}
