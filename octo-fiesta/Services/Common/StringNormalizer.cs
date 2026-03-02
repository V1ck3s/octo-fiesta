using System.Text;

namespace octo_fiesta.Services.Common;

/// <summary>
/// Helper class for normalizing strings for comparison purposes.
/// Handles different quote characters (straight vs curly quotes) and other variants.
/// </summary>
public static class StringNormalizer
{
    // Mapping of various quote and apostrophe characters to their canonical forms
    private static readonly Dictionary<char, char> QuoteNormalizations = new()
    {
        // Curly quotes to straight quotes
        { '‘', '\'' },
        { '’', '\'' },
        { '“', '"' },
        { '”', '"' },
        { '′', '\'' },
        { '″', '"' },
        
        // Backticks to straight quotes
        { '`', '\'' }
    };

    /// <summary>
    /// Normalizes a string for comparison by standardizing quote characters.
    /// Converts various forms of apostrophes and quotes to their canonical straight forms.
    /// This allows matching titles like "Jenna's" with "Jenna`s".
    /// </summary>
    /// <param name="input">String to normalize.</param>
    /// <returns>Normalized string comparison.</returns>
    public static string NormalizeForComparison(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        var sb = new StringBuilder(input.Length);

        foreach (var c in input)
        {
            if (QuoteNormalizations.TryGetValue(c, out var normalized))
            {
                sb.Append(normalized);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Creates a normalized comparison key for a string.
    /// Useful for HashSet lookups with normalized values.
    /// </summary>
    /// <param name="input">String to create a key for.</param>
    /// <returns>Normalized string suitable for case-insensitive comparison.</returns>
    public static string CreateComparisonKey(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        return NormalizeForComparison(input).ToLowerInvariant();
    }
}


