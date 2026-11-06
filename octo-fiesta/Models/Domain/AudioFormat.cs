namespace octo_fiesta.Models.Domain;

/// <summary>
/// The format a track is served in, as Subsonic clients see it: file suffix, MIME type and
/// bit rate in kbps.
/// </summary>
public sealed record AudioFormat(string Suffix, string ContentType, int BitRate)
{
    public static readonly AudioFormat Mp3_128 = new("mp3", "audio/mpeg", 128);
    public static readonly AudioFormat Mp3_320 = new("mp3", "audio/mpeg", 320);

    /// <summary>
    /// FLAC at the given resolution. The bit rate is the uncompressed PCM rate (1411 kbps for
    /// CD quality), the ceiling of what the FLAC stream needs.
    /// </summary>
    public static AudioFormat Flac(int bitDepth = 16, double sampleRateKhz = 44.1) =>
        new("flac", "audio/flac", (int)Math.Round(bitDepth * sampleRateKhz * 2));

    /// <summary>
    /// AAC in an MP4 container at the given bit rate.
    /// </summary>
    public static AudioFormat Aac(int bitRate) => new("m4a", "audio/mp4", bitRate);

    /// <summary>
    /// The same audio delivered in an MP4 container (FLAC inside MP4, as Tidal's DASH streams are).
    /// </summary>
    public AudioFormat InMp4() => this with { Suffix = "m4a", ContentType = "audio/mp4" };
}
