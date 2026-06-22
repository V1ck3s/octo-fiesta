using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Primp;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>Solves the ALTCHA v2 proof-of-work that gates qobuz.squid.wtf /api/download-music.</summary>
public partial class SquidWTFCaptchaSolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SquidWTFCaptchaSolver> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Server-side cookie is valid 30 min; refresh slightly earlier to absorb clock skew.
    private static readonly TimeSpan CookieValidity = TimeSpan.FromMinutes(28);
    private const int MaxSolverIterations = 1_000_000;

    private string? _cookieHeader;
    private DateTimeOffset _cookieExpiresAt = DateTimeOffset.MinValue;

    // Token-based captcha cache (Amazon Music uses X-Captcha-Token instead of a cookie)
    private string? _captchaToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan TokenValidity = TimeSpan.FromMinutes(13);
    private static readonly TimeSpan CaptchaRateLimitCooldown = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _amazonSessionLock = new(1, 1);

    private SquidWTFAmazonSession? _amazonSession;
    private string? _amazonSessionBaseUrl;
    private DateTimeOffset _captchaRateLimitedUntil = DateTimeOffset.MinValue;

    private const string AmazonCaptchaTokenHeader = "X-Captcha-Token";

    public SquidWTFCaptchaSolver(
        IHttpClientFactory httpClientFactory,
        ILogger<SquidWTFCaptchaSolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> GetCaptchaCookieAsync(
        string baseUrl,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cookieHeader != null && DateTimeOffset.UtcNow < _cookieExpiresAt)
        {
            return _cookieHeader;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cookieHeader != null && DateTimeOffset.UtcNow < _cookieExpiresAt)
            {
                return _cookieHeader;
            }

            _cookieHeader = await SolveAndVerifyAsync(baseUrl, cancellationToken);
            _cookieExpiresAt = DateTimeOffset.UtcNow + CookieValidity;
            return _cookieHeader;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns an X-Captcha-Token value for Amazon Music (amz.squid.wtf).
    /// Uses /api/captcha/challenge + /api/captcha/verify → { token }.
    /// </summary>
    public async Task<string> GetAmazonCaptchaTokenAsync(
        string baseUrl,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var session = await GetAmazonSessionAsync(baseUrl, cancellationToken);
        return await RefreshAmazonCaptchaTokenAsync(session, baseUrl.TrimEnd('/'), forceRefresh, cancellationToken);
    }

    /// <summary>
    /// POST to an Amazon SquidWTF API route using the shared browser session (cookies + captcha token).
    /// </summary>
    public async Task<HttpResponseMessage> SendAmazonPostAsync(
        string baseUrl,
        string path,
        string jsonBody,
        CancellationToken cancellationToken = default)
    {
        var session = await GetAmazonSessionAsync(baseUrl, cancellationToken);
        var response = await SendAmazonPostCoreAsync(session, baseUrl, path, jsonBody, forceRefreshToken: false, cancellationToken);

        // 403 is not fixed by a new token (e.g. "web interface only"); only retry on 401.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            response = await SendAmazonPostCoreAsync(session, baseUrl, path, jsonBody, forceRefreshToken: true, cancellationToken);
        }

        return response;
    }

    private async Task<string> RefreshAmazonCaptchaTokenAsync(
        SquidWTFAmazonSession session,
        string baseUrl,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (!forceRefresh &&
            session.CaptchaToken != null &&
            DateTimeOffset.UtcNow < session.CaptchaTokenExpiresAt)
        {
            return session.CaptchaToken;
        }

        if (DateTimeOffset.UtcNow < _captchaRateLimitedUntil)
        {
            throw new InvalidOperationException(
                $"Amazon captcha is rate-limited until {_captchaRateLimitedUntil:u}. Please wait before retrying.");
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh &&
                session.CaptchaToken != null &&
                DateTimeOffset.UtcNow < session.CaptchaTokenExpiresAt)
            {
                return session.CaptchaToken;
            }

            if (DateTimeOffset.UtcNow < _captchaRateLimitedUntil)
            {
                throw new InvalidOperationException(
                    $"Amazon captcha is rate-limited until {_captchaRateLimitedUntil:u}. Please wait before retrying.");
            }

            if (forceRefresh)
            {
                session.CaptchaToken = null;
                session.CaptchaTokenExpiresAt = DateTimeOffset.MinValue;
                _captchaToken = null;
                _tokenExpiresAt = DateTimeOffset.MinValue;
            }

            return await SolveAndVerifyAmazonCaptchaAsync(session, baseUrl, ct);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> SolveAndVerifyAmazonCaptchaAsync(
        SquidWTFAmazonSession session,
        string baseUrl,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var challengeHeaders = CreateAmazonBrowserHeaders(baseUrl);
        challengeHeaders["accept"] = "application/json, text/plain, */*";

        using var challengeResp = await session.Client.GetAsync($"{baseUrl}/api/captcha/challenge", challengeHeaders);
        if (challengeResp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var challengeError = challengeResp.ReadAsString();
            MarkCaptchaRateLimited();
            throw new InvalidOperationException(
                $"Amazon captcha challenge returned 429: {challengeError}");
        }

        if (challengeResp.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Amazon captcha challenge returned {(int)challengeResp.StatusCode}: {challengeResp.ReadAsString()}");
        }

        var challengeJson = challengeResp.ReadAsString().TrimEnd();

        using var challengeDoc = JsonDocument.Parse(challengeJson);
        var root = challengeDoc.RootElement;

        JsonElement parameters;
        if (root.TryGetProperty("parameters", out var parametersEl))
            parameters = parametersEl;
        else
            parameters = root;

        var (counter, derivedKeyHex, elapsedMs) = SolveChallenge(parameters, ct);

        // Match browser/Python payload: time as float ms, challenge JSON embedded verbatim.
        var solutionJson = JsonSerializer.Serialize(new { counter, derivedKey = derivedKeyHex, time = (double)elapsedMs });
        var payloadJson = $"{{\"challenge\":{challengeJson},\"solution\":{solutionJson}}}";
        var payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var verifyBody = JsonSerializer.Serialize(new { payload = payloadB64, webNonce = session.WebNonce });

        _logger.LogInformation("Amazon captcha: solved in {ElapsedMs}ms (counter={Counter}), verifying...", elapsedMs, counter);
        using var verifyResp = await session.Client.PostAsync(
            $"{baseUrl}/api/captcha/verify",
            verifyBody,
            "application/json");

        var verifyJson = verifyResp.ReadAsString();
        _logger.LogDebug("Amazon captcha verify response ({Status}): {Json}", (int)verifyResp.StatusCode, verifyJson);

        if (verifyResp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            MarkCaptchaRateLimited();
            throw new InvalidOperationException(
                $"Amazon captcha verify returned 429: {verifyJson}");
        }

        if (verifyResp.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Amazon captcha verify returned {(int)verifyResp.StatusCode}: {verifyJson}");
        }

        using var verifyDoc = JsonDocument.Parse(verifyJson);
        if (!verifyDoc.RootElement.TryGetProperty("token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Amazon captcha verify did not return a token. Response: {verifyJson}");
        }

        var token = tokenElement.GetString()
            ?? throw new InvalidOperationException("Amazon captcha token is null");

        session.CaptchaToken = token;
        session.CaptchaTokenExpiresAt = DateTimeOffset.UtcNow + TokenValidity;
        _captchaToken = token;
        _tokenExpiresAt = session.CaptchaTokenExpiresAt;

        _logger.LogInformation(
            "Amazon Music captcha solved in {ElapsedMs}ms (counter={Counter}), session valid ~{Minutes} min",
            elapsedMs, counter, (int)TokenValidity.TotalMinutes);

        return token;
    }

    private void MarkCaptchaRateLimited()
    {
        _captchaRateLimitedUntil = DateTimeOffset.UtcNow + CaptchaRateLimitCooldown;
        _logger.LogWarning(
            "Amazon captcha rate-limited by upstream; backing off until {Until:u}",
            _captchaRateLimitedUntil);
    }

    private async Task<HttpResponseMessage> SendAmazonPostCoreAsync(
        SquidWTFAmazonSession session,
        string baseUrl,
        string path,
        string jsonBody,
        bool forceRefreshToken,
        CancellationToken ct)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var token = await RefreshAmazonCaptchaTokenAsync(session, trimmed, forceRefreshToken, ct);

        var headers = CreateAmazonSearchHeaders(trimmed, token);
        using var primpResp = await session.Client.PostAsync($"{trimmed}{path}", jsonBody, "application/json", headers);
        ct.ThrowIfCancellationRequested();

        var responseBody = primpResp.ReadAsString();
        return new HttpResponseMessage(primpResp.StatusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }

    private async Task<SquidWTFAmazonSession> GetAmazonSessionAsync(string baseUrl, CancellationToken ct)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (_amazonSession != null && _amazonSessionBaseUrl == trimmed && !string.IsNullOrEmpty(_amazonSession.WebNonce))
            return _amazonSession;

        await _amazonSessionLock.WaitAsync(ct);
        try
        {
            if (_amazonSession == null || _amazonSessionBaseUrl != trimmed)
            {
                _amazonSession?.Dispose();
                _amazonSession = new SquidWTFAmazonSession();
                _amazonSessionBaseUrl = trimmed;
                _captchaToken = null;
                _tokenExpiresAt = DateTimeOffset.MinValue;
            }

            if (string.IsNullOrEmpty(_amazonSession.WebNonce))
            {
                _amazonSession.WebNonce = await LoadAmazonPageSessionAsync(_amazonSession.Client, trimmed, ct);
            }

            return _amazonSession;
        }
        finally
        {
            _amazonSessionLock.Release();
        }
    }

    private static Dictionary<string, string> CreateAmazonBrowserHeaders(string baseUrl) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["origin"] = baseUrl,
            ["referer"] = $"{baseUrl}/",
            ["sec-fetch-site"] = "same-origin",
            ["sec-fetch-mode"] = "cors",
            ["sec-fetch-dest"] = "empty",
        };

    private static Dictionary<string, string> CreateAmazonSearchHeaders(string baseUrl, string captchaToken)
    {
        var headers = CreateAmazonBrowserHeaders(baseUrl);
        headers["accept"] = "application/json, text/plain, */*";
        headers[AmazonCaptchaTokenHeader] = captchaToken;
        return headers;
    }

    private static async Task<string> LoadAmazonPageSessionAsync(PrimpClient client, string baseUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var pageResponse = await client.GetAsync($"{baseUrl}/", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        });

        if (pageResponse.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Amazon homepage returned {(int)pageResponse.StatusCode}");
        }

        var html = pageResponse.ReadAsString();

        var match = AmazonWebNonceRegex().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "Amazon page session nonce (window.__AMZ_WEB.n) was not found in homepage HTML");
        }

        return match.Groups[1].Value;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"window\.__AMZ_WEB\s*=\s*\{\s*""n""\s*:\s*""([^""]+)""")]
    private static partial System.Text.RegularExpressions.Regex AmazonWebNonceRegex();

    private async Task<string> SolveAndVerifyAsync(string baseUrl, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient();
        var trimmed = baseUrl.TrimEnd('/');

        var challengeJson = await http.GetStringAsync($"{trimmed}/api/altcha/challenge", ct);
        using var challengeDoc = JsonDocument.Parse(challengeJson);
        var parameters = challengeDoc.RootElement.GetProperty("parameters");

        var (counter, derivedKeyHex, elapsedMs) = SolveChallenge(parameters, ct);

        var solutionJson = JsonSerializer.Serialize(new { counter, derivedKey = derivedKeyHex, time = elapsedMs });
        var payloadJson = $"{{\"challenge\":{challengeJson.TrimEnd()},\"solution\":{solutionJson}}}";
        var payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var verifyBody = JsonSerializer.Serialize(new { payload = payloadB64 });

        using var content = new StringContent(verifyBody, Encoding.UTF8, "application/json");
        using var verifyResp = await http.PostAsync($"{trimmed}/api/altcha/verify", content, ct);
        verifyResp.EnsureSuccessStatusCode();

        if (!verifyResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            throw new InvalidOperationException("captcha verify response did not set any cookies");
        }

        var captchaCookie = setCookies
            .Select(c => c.Split(';')[0].Trim())
            .FirstOrDefault(c => c.StartsWith("captcha_verified_at=", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("captcha verify did not set captcha_verified_at cookie");

        _logger.LogInformation(
            "SquidWTF Qobuz captcha solved in {ElapsedMs}ms (counter={Counter}), session valid ~28 min",
            elapsedMs, counter);

        return captchaCookie;
    }

    /// <summary>
    /// ALTCHA v2 solver. Supports two algorithm variants:
    ///   - "PBKDF2/SHA-256": password = nonce+counter, PBKDF2 derive, check prefix (amz.squid.wtf)
    ///   - chained SHA-256 (legacy, no algorithm field): salt+nonce+counter hashed `cost` times (qobuz.squid.wtf)
    /// The server picks a solution counter in advance; we find it by iterating from 0.
    /// </summary>
    public static (int Counter, string DerivedKeyHex, long ElapsedMs) SolveChallenge(
        JsonElement parameters,
        CancellationToken ct)
    {
        var nonce = Convert.FromHexString(parameters.GetProperty("nonce").GetString()!);
        var salt = Convert.FromHexString(parameters.GetProperty("salt").GetString()!);
        var cost = parameters.GetProperty("cost").GetInt32();
        var keyLength = parameters.GetProperty("keyLength").GetInt32();
        var keyPrefix = Convert.FromHexString(parameters.GetProperty("keyPrefix").GetString()!);

        var algorithm = parameters.TryGetProperty("algorithm", out var algEl) ? algEl.GetString() : null;

        // password buf: nonce bytes followed by 4-byte big-endian counter
        var password = new byte[nonce.Length + 4];
        Array.Copy(nonce, password, nonce.Length);

        var sw = Stopwatch.StartNew();

        if (algorithm?.StartsWith("PBKDF2", StringComparison.OrdinalIgnoreCase) == true)
        {
            // PBKDF2/SHA-256 variant (amz.squid.wtf)
            for (var counter = 0; counter < MaxSolverIterations; counter++)
            {
                ct.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt32BigEndian(password.AsSpan(nonce.Length), (uint)counter);

                var derived = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    cost,
                    HashAlgorithmName.SHA256,
                    keyLength);

                if (derived.AsSpan(0, keyPrefix.Length).SequenceEqual(keyPrefix))
                    return (counter, Convert.ToHexString(derived).ToLowerInvariant(), sw.ElapsedMilliseconds);
            }
        }
        else
        {
            // Chained SHA-256 variant (qobuz.squid.wtf, no algorithm field)
            var initial = new byte[salt.Length + password.Length];
            Array.Copy(salt, 0, initial, 0, salt.Length);

            var derived = new byte[keyLength];
            Span<byte> hashBuf = stackalloc byte[32];

            for (var counter = 0; counter < MaxSolverIterations; counter++)
            {
                ct.ThrowIfCancellationRequested();

                BinaryPrimitives.WriteUInt32BigEndian(password.AsSpan(nonce.Length), (uint)counter);
                Array.Copy(password, 0, initial, salt.Length, password.Length);

                SHA256.HashData(initial, hashBuf);
                hashBuf[..keyLength].CopyTo(derived);

                for (var i = 1; i < cost; i++)
                {
                    SHA256.HashData(derived, hashBuf);
                    hashBuf[..keyLength].CopyTo(derived);
                }

                if (derived.AsSpan(0, keyPrefix.Length).SequenceEqual(keyPrefix))
                    return (counter, Convert.ToHexString(derived).ToLowerInvariant(), sw.ElapsedMilliseconds);
            }
        }

        throw new InvalidOperationException(
            $"captcha solver exhausted {MaxSolverIterations} iterations without finding a match");
    }
}
