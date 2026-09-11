using System.Text;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Keeps only the external playlists that actually answer the query.
///
/// Provider playlist search never returns an empty list: with no real match,
/// Qobuz pads its response with unrelated editorial playlists. Because search3
/// has no playlist field, those are merged into the ALBUM section, so the
/// padding reads as bogus albums sitting next to the user's real results.
///
/// The rule is deliberately blunt: every word of the query must appear in the
/// playlist name or in its curator, ignoring case, accents and punctuation.
/// Measured against live Qobuz responses on 2026-09-07, it keeps the five
/// genuine "gainsbourg" playlists and drops the ten fillers returned for
/// "keen v" and "columbine".
/// </summary>
public static class PlaylistRelevanceFilter
{
    /// <summary>
    /// Returns the playlists relevant to <paramref name="query"/>. A blank query
    /// carries nothing to judge relevance against, so the list is left untouched.
    /// </summary>
    public static List<ExternalPlaylist> Apply(string? query, IEnumerable<ExternalPlaylist>? playlists)
    {
        var candidates = playlists?.ToList() ?? new List<ExternalPlaylist>();
        var terms = Tokenize(query);

        return terms.Count == 0
            ? candidates
            : candidates.Where(p => Matches(terms, p)).ToList();
    }

    private static bool Matches(List<string> terms, ExternalPlaylist playlist)
    {
        var haystack = Flatten($"{playlist.Name} {playlist.CuratorName}");
        return terms.All(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static List<string> Tokenize(string? query)
        => Flatten(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>
    /// Turns every non-alphanumeric character of the comparison key into a space, so
    /// "Gainsbourg's Got Class(ique)" and "gainsbourg got class" compare on the same
    /// footing.
    /// </summary>
    private static string Flatten(string? input)
    {
        var key = StringNormalizer.CreateComparisonKey(input);
        var sb = new StringBuilder(key.Length);

        foreach (var c in key)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return sb.ToString();
    }
}
