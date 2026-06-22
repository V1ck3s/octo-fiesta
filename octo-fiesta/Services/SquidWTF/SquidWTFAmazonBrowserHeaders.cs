namespace octo_fiesta.Services.SquidWTF;

/// <summary>Per-request Chrome 131 headers for Amazon SquidWTF (TLS is handled by curl_amz_tls).</summary>
internal static class SquidWTFAmazonBrowserHeaders
{
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string SecChUa =
        "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"";

    public static Dictionary<string, string> CreatePageHeaders() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["accept"] =
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7",
            ["user-agent"] = UserAgent,
            ["sec-ch-ua"] = SecChUa,
            ["sec-ch-ua-mobile"] = "?0",
            ["sec-ch-ua-platform"] = "\"macOS\"",
            ["sec-fetch-site"] = "none",
            ["sec-fetch-mode"] = "navigate",
            ["sec-fetch-dest"] = "document",
            ["accept-language"] = "en-US,en;q=0.9",
        };

    public static Dictionary<string, string> CreateChallengeHeaders() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["accept"] = "application/json, text/plain, */*",
            ["user-agent"] = UserAgent,
            ["sec-ch-ua"] = SecChUa,
            ["sec-ch-ua-mobile"] = "?0",
            ["sec-ch-ua-platform"] = "\"macOS\"",
            ["accept-language"] = "en-US,en;q=0.9",
        };

    public static Dictionary<string, string> CreateVerifyHeaders() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["user-agent"] = UserAgent,
            ["sec-ch-ua"] = SecChUa,
            ["sec-ch-ua-mobile"] = "?0",
            ["sec-ch-ua-platform"] = "\"macOS\"",
            ["accept-language"] = "en-US,en;q=0.9",
        };

    public static Dictionary<string, string> CreateApiPostHeaders(string baseUrl, string captchaToken) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["accept"] = "application/json, text/plain, */*",
            ["user-agent"] = UserAgent,
            ["origin"] = baseUrl,
            ["referer"] = $"{baseUrl}/",
            ["sec-ch-ua"] = SecChUa,
            ["sec-ch-ua-mobile"] = "?0",
            ["sec-ch-ua-platform"] = "\"macOS\"",
            ["sec-fetch-site"] = "same-origin",
            ["sec-fetch-mode"] = "cors",
            ["sec-fetch-dest"] = "empty",
            ["accept-language"] = "en-US,en;q=0.9",
            ["X-Captcha-Token"] = captchaToken,
        };
}
