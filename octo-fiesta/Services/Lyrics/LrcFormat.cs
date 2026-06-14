using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using octo_fiesta.Models.Domain;

namespace octo_fiesta.Services.Lyrics;

/// <summary>
/// Parsing and serialization helpers for the LRC lyrics format.
/// Pure functions, no I/O — covered by unit tests.
/// </summary>
public static partial class LrcFormat
{
    // One [mm:ss.xx] / [mm:ss.xxx] / [mm:ss] timestamp. Several may prefix a single line.
    [GeneratedRegex(@"\[(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    /// <summary>
    /// Parses a synced LRC document into timestamped lines, sorted by time.
    /// A line may carry several timestamps (repeated lyrics); each yields a line.
    /// Lines without a timestamp (metadata tags like [ar:], [length:]) are ignored.
    /// </summary>
    public static List<LyricLine> ParseSynced(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrEmpty(lrc))
        {
            return lines;
        }

        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = TimestampRegex().Matches(raw);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = TimestampRegex().Replace(raw, string.Empty).Trim();
            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = 0;
                if (m.Groups[3].Success)
                {
                    // Normalize fraction to milliseconds (1 digit = tenths, 2 = centiseconds, 3 = ms).
                    var frac = m.Groups[3].Value;
                    frac = frac.Length switch
                    {
                        1 => frac + "00",
                        2 => frac + "0",
                        _ => frac[..3]
                    };
                    fraction = int.Parse(frac, CultureInfo.InvariantCulture);
                }

                var startMs = (((long)minutes * 60) + seconds) * 1000 + fraction;
                lines.Add(new LyricLine(startMs, text));
            }
        }

        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return lines;
    }

    /// <summary>
    /// Splits a plain (unsynced) lyrics block into lines, all with StartMs 0.
    /// </summary>
    public static List<LyricLine> ParsePlain(string? plain)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrEmpty(plain))
        {
            return lines;
        }

        foreach (var raw in plain.Replace("\r\n", "\n").Split('\n'))
        {
            lines.Add(new LyricLine(0, raw.TrimEnd()));
        }

        return lines;
    }

    /// <summary>
    /// Serializes lyrics to an LRC document for a .lrc sidecar file.
    /// Synced lyrics get [mm:ss.xx] timestamps; plain lyrics are written as-is.
    /// </summary>
    public static string ToLrc(SongLyrics lyrics)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(lyrics.DisplayArtist))
        {
            sb.Append("[ar:").Append(lyrics.DisplayArtist).AppendLine("]");
        }
        if (!string.IsNullOrEmpty(lyrics.DisplayTitle))
        {
            sb.Append("[ti:").Append(lyrics.DisplayTitle).AppendLine("]");
        }

        foreach (var line in lyrics.Lines)
        {
            if (lyrics.Synced)
            {
                var ts = TimeSpan.FromMilliseconds(line.StartMs);
                sb.Append('[')
                    .Append(((int)ts.TotalMinutes).ToString("D2", CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(ts.Seconds.ToString("D2", CultureInfo.InvariantCulture))
                    .Append('.')
                    .Append((ts.Milliseconds / 10).ToString("D2", CultureInfo.InvariantCulture))
                    .Append(']');
            }

            sb.AppendLine(line.Text);
        }

        return sb.ToString();
    }
}
