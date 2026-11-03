namespace octo_fiesta.Services.Common;

/// <summary>
/// Recognizes album barcodes (UPC-A, EAN-13, GTIN-14) in search queries.
/// </summary>
public static class BarcodeHelper
{
    /// <summary>
    /// True when the query is a 12-14 digit code with a valid GTIN check digit, so plain
    /// numeric searches (years, track titles like "1999") are not mistaken for barcodes.
    /// </summary>
    public static bool IsBarcode(string? query)
    {
        if (string.IsNullOrEmpty(query) || query.Length < 12 || query.Length > 14)
        {
            return false;
        }

        if (!query.All(char.IsAsciiDigit))
        {
            return false;
        }

        // GTIN check digit: weights 3,1,3,... from the digit left of the check digit
        var sum = 0;
        for (var i = query.Length - 2; i >= 0; i--)
        {
            var weight = (query.Length - 2 - i) % 2 == 0 ? 3 : 1;
            sum += (query[i] - '0') * weight;
        }

        return (10 - sum % 10) % 10 == query[^1] - '0';
    }

    /// <summary>
    /// The forms a provider may store the barcode in: EAN-13 (zero-padded) first, then UPC-A.
    /// </summary>
    public static IReadOnlyList<string> Variants(string barcode)
    {
        var payload = barcode.TrimStart('0');
        var variants = new List<string>();
        foreach (var length in new[] { 13, 12 })
        {
            if (payload.Length <= length)
            {
                variants.Add(payload.PadLeft(length, '0'));
            }
        }

        return variants;
    }

    /// <summary>
    /// True when two barcodes name the same product, ignoring leading zeros.
    /// </summary>
    public static bool AreEqual(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        a.TrimStart('0') == b.TrimStart('0');
}
