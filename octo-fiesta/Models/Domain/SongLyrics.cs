namespace octo_fiesta.Models.Domain;

/// <summary>
/// One lyric line. <see cref="StartMs"/> is the offset from the start of the
/// track in milliseconds (0 for unsynced lyrics).
/// </summary>
public record LyricLine(long StartMs, string Text);

/// <summary>
/// Lyrics for a single song, synced (timestamped) or plain.
/// Maps to the OpenSubsonic <c>structuredLyrics</c> object.
/// </summary>
public class SongLyrics
{
    public string DisplayArtist { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;

    /// <summary>ISO-639 language code, or "xxx" when unknown (OpenSubsonic default).</summary>
    public string Lang { get; set; } = "xxx";

    /// <summary>Global timing offset in milliseconds (OpenSubsonic). Usually 0.</summary>
    public long Offset { get; set; }

    /// <summary>True when the lines carry timestamps.</summary>
    public bool Synced { get; set; }

    /// <summary>The lyric lines, in order. For synced lyrics each carries a StartMs.</summary>
    public List<LyricLine> Lines { get; set; } = new();

    public bool HasContent => Lines.Count > 0;
}
