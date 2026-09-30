using System.Text.RegularExpressions;

namespace octo_fiesta.Services.Common;

public static partial class StringNormalizer
{
    private static readonly Regex TrailingFeatSuffixRegex = new(
        @"\s+[\(\[](feat\.|ft\.)[^\)\]]*[\)\]]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public static string CreateArtistComparisonKey(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        return NormalizeForComparison(input);
    }

    public static string CreateSongTitleDedupeKey(string? title)
    {
        var s = CreateComparisonKey(title);
        while (true)
        {
            var next = TrailingFeatSuffixRegex.Replace(s, "").TrimEnd();
            if (next.Length == s.Length && next == s)
            {
                return s;
            }
            s = next;
        }
    }
}
