using System.Text.RegularExpressions;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Decides whether a library track is the same recording as an external one when their tags
/// come from different sources. Providers decorate titles with edition labels ("Album Version",
/// "Explicit", "2011 Remaster") and credit only the primary artist, while library managers such
/// as beets or Picard often strip those labels and list every featured artist.
/// Labels that change the recording (Live, Remix, Acoustic, Cover, ...) are kept, so those
/// versions never pass as the original.
/// </summary>
public static class TrackMatcher
{
    /// <summary>Tolerance applied to loose (non-identical) matches when both durations are known.</summary>
    public const int DurationToleranceSeconds = 5;

    private static readonly Regex EditionLabel = new(
        @"^(?:" +
        @"(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?re-?master(?:ed)?(?:\s+version)?(?:\s+\d{4})?(?:\s+version)?" +
        @"|(?:album|single|lp|original)\s+version" +
        @"|explicit(?:\s+version)?|clean(?:\s+version)?|edited(?:\s+version)?" +
        @"|censored|uncensored|dirty" +
        @"|deluxe(?:\s+(?:edition|version))?|expanded\s+edition" +
        @"|mono|stereo" +
        @")$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FeatLabel = new(
        @"^(?:feat\.?|ft\.?|featuring|with)\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InlineFeat = new(
        @"\s+(?:feat\.|ft\.|featuring)\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ArtistSeparator = new(
        @"\s*(?:,|;|/|&|\s(?:feat\.?|ft\.?|featuring)\s)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Comparison key for a title with edition and featuring labels removed.
    /// "Gold Digger (Album Version (Explicit))" and "Gold Digger" give the same key;
    /// "Diamonds (Remix - Album Version)" and "Diamonds - Remix" give "diamonds (remix)".
    /// </summary>
    public static string CreateTitleKey(string? title)
    {
        var (baseTitle, qualifiers) = Parse(StringNormalizer.CreateComparisonKey(title));
        return qualifiers.Count == 0 ? baseTitle : $"{baseTitle} ({string.Join(") (", qualifiers)})";
    }

    /// <summary>Title without edition labels, for building search queries.</summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        return Parse(StringNormalizer.NormalizeForComparison(title)).BaseTitle;
    }

    /// <summary>Comparison keys of every artist credited in the given strings.</summary>
    public static HashSet<string> CreateArtistKeys(params string?[] artists)
    {
        var keys = new HashSet<string>();
        foreach (var artist in artists)
        {
            var key = StringNormalizer.CreateComparisonKey(artist).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            keys.Add(key);
            foreach (var part in ArtistSeparator.Split(key))
            {
                if (part.Length > 0)
                {
                    keys.Add(part);
                }
            }
        }

        return keys;
    }

    /// <summary>First credited artist, for building search queries.</summary>
    public static string PrimaryArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return "";
        }

        var first = ArtistSeparator.Split(artist.Trim())[0];
        return first.Length > 0 ? first : artist.Trim();
    }

    /// <summary>
    /// True when the candidate is the same recording: equal ISRC, or equal cleaned title with
    /// an overlapping artist credit. Loose title or artist matches must also agree on duration
    /// when both durations are known.
    /// </summary>
    public static bool IsMatch(TrackInfo external, TrackInfo candidate)
    {
        if (!string.IsNullOrEmpty(external.Isrc) &&
            candidate.Isrcs.Any(i => string.Equals(i, external.Isrc, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var externalTitle = StringNormalizer.CreateComparisonKey(external.Title);
        var candidateTitle = StringNormalizer.CreateComparisonKey(candidate.Title);
        var externalArtist = StringNormalizer.CreateComparisonKey(external.Artist);
        var candidateArtist = StringNormalizer.CreateComparisonKey(candidate.Artist);

        if (externalTitle.Length == 0 || externalArtist.Length == 0)
        {
            return false;
        }

        // Identical tags: the historical rule, no duration check.
        if (externalTitle == candidateTitle && externalArtist == candidateArtist)
        {
            return true;
        }

        if (CreateTitleKey(external.Title) != CreateTitleKey(candidate.Title))
        {
            return false;
        }

        var externalArtists = CreateArtistKeys(external.Artist);
        var candidateArtists = CreateArtistKeys(new[] { candidate.Artist }.Concat(candidate.Artists).ToArray());
        var externalPrimary = StringNormalizer.CreateComparisonKey(PrimaryArtist(external.Artist));
        var candidatePrimary = StringNormalizer.CreateComparisonKey(PrimaryArtist(candidate.Artist));

        if (!candidateArtists.Contains(externalPrimary) && !externalArtists.Contains(candidatePrimary))
        {
            return false;
        }

        if (external.Duration is > 0 && candidate.Duration is > 0 &&
            Math.Abs(external.Duration.Value - candidate.Duration.Value) > DurationToleranceSeconds)
        {
            return false;
        }

        return true;
    }

    private static (string BaseTitle, List<string> Qualifiers) Parse(string title)
    {
        var qualifiers = new List<string>();
        var rest = title.Trim();

        // Trailing bracket groups, innermost content parsed recursively.
        while (TryTakeTrailingGroup(rest, out var before, out var content))
        {
            rest = before;
            AddQualifier(qualifiers, content);
        }

        // Spotify-style " - Remastered 2011" / " - Remix" suffixes.
        var dash = rest.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            foreach (var part in rest[(dash + 3)..].Split(" - "))
            {
                AddQualifier(qualifiers, part);
            }

            rest = rest[..dash];
        }

        rest = InlineFeat.Replace(rest, "").Trim();
        qualifiers.Reverse();
        return (rest, qualifiers);
    }

    private static void AddQualifier(List<string> qualifiers, string content)
    {
        // "(feat. A, B)" credits artists, it does not describe the recording.
        if (FeatLabel.IsMatch(content.Trim() + " "))
        {
            return;
        }

        foreach (var raw in content.Split(new[] { " - ", ", ", " / " }, StringSplitOptions.None))
        {
            var part = raw.Trim();
            if (part.Length == 0 || FeatLabel.IsMatch(part + " "))
            {
                continue;
            }

            // "Album Version (Explicit)": every piece must be an edition label to be dropped.
            var (inner, innerQualifiers) = Parse(part);
            if (EditionLabel.IsMatch(inner) && innerQualifiers.Count == 0)
            {
                continue;
            }

            qualifiers.AddRange(innerQualifiers.AsEnumerable().Reverse());
            if (!EditionLabel.IsMatch(inner))
            {
                qualifiers.Add(inner);
            }
        }
    }

    private static bool TryTakeTrailingGroup(string text, out string before, out string content)
    {
        before = text;
        content = "";
        if (text.Length < 3)
        {
            return false;
        }

        var close = text[^1];
        if (close != ')' && close != ']')
        {
            return false;
        }

        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == ')' || c == ']')
            {
                depth++;
            }
            else if (c == '(' || c == '[')
            {
                depth--;
                if (depth == 0)
                {
                    if (i == 0)
                    {
                        return false; // the whole title is bracketed
                    }

                    before = text[..i].TrimEnd();
                    content = text[(i + 1)..^1];
                    return before.Length > 0;
                }
            }
        }

        return false;
    }
}

/// <summary>Tags of one track as seen by <see cref="TrackMatcher"/>.</summary>
public record TrackInfo(
    string? Title,
    string? Artist,
    int? Duration = null,
    string? Isrc = null,
    IReadOnlyList<string>? ArtistNames = null,
    IReadOnlyList<string>? IsrcList = null)
{
    public IReadOnlyList<string> Artists => ArtistNames ?? Array.Empty<string>();
    public IReadOnlyList<string> Isrcs => IsrcList ?? (string.IsNullOrEmpty(Isrc) ? Array.Empty<string>() : new[] { Isrc });
}
