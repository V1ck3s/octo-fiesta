using System.Diagnostics;
using System.Net;

namespace octo_fiesta.Services.SquidWTF;

/// <summary>
/// Amazon SquidWTF HTTP via curl-impersonate (Chrome TLS). Works on Debian/glibc 2.36 Docker images.
/// </summary>
internal sealed class SquidWTFAmazonImpersonateHttp : IDisposable
{
    private readonly string _cookieJarPath;
    private readonly string _curlBinary;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _disposed;

    public SquidWTFAmazonImpersonateHttp()
    {
        _cookieJarPath = Path.Combine(Path.GetTempPath(), $"amz-sess-{Guid.NewGuid():N}.cookies");
        _curlBinary = ResolveCurlBinary();
    }

    public Task<AmazonImpersonateResponse> GetAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct) =>
        SendAsync("GET", url, body: null, contentType: null, headers, ct);

    public Task<AmazonImpersonateResponse> PostAsync(
        string url,
        string body,
        string contentType,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct) =>
        SendAsync("POST", url, body, contentType, headers, ct);

    private async Task<AmazonImpersonateResponse> SendAsync(
        string method,
        string url,
        string? body,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        await _requestLock.WaitAsync(ct);
        try
        {
            var bodyFile = Path.GetTempFileName();
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = _curlBinary,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = body != null,
                    UseShellExecute = false,
                };

                process.StartInfo.ArgumentList.Add("--silent");
                process.StartInfo.ArgumentList.Add("--show-error");
                process.StartInfo.ArgumentList.Add("--cookie");
                process.StartInfo.ArgumentList.Add(_cookieJarPath);
                process.StartInfo.ArgumentList.Add("--cookie-jar");
                process.StartInfo.ArgumentList.Add(_cookieJarPath);
                process.StartInfo.ArgumentList.Add("-o");
                process.StartInfo.ArgumentList.Add(bodyFile);
                process.StartInfo.ArgumentList.Add("-w");
                process.StartInfo.ArgumentList.Add("%{http_code}");
                process.StartInfo.ArgumentList.Add("-X");
                process.StartInfo.ArgumentList.Add(method);

                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                    {
                        process.StartInfo.ArgumentList.Add("-H");
                        process.StartInfo.ArgumentList.Add($"{key}: {value}");
                    }
                }

                if (body != null)
                {
                    if (headers == null ||
                        !headers.Keys.Any(k => k.Equals("content-type", StringComparison.OrdinalIgnoreCase)))
                    {
                        process.StartInfo.ArgumentList.Add("-H");
                        process.StartInfo.ArgumentList.Add($"Content-Type: {contentType ?? "application/json"}");
                    }

                    process.StartInfo.ArgumentList.Add("--data-binary");
                    process.StartInfo.ArgumentList.Add("@-");
                }

                process.StartInfo.ArgumentList.Add(url);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to start curl-impersonate ({_curlBinary}). " +
                        "Install curl_amz_tls in the container or set SQUIDWTF_CURL_IMPERSONATE.",
                        ex);
                }

                if (body != null)
                {
                    await process.StandardInput.WriteAsync(body.AsMemory(), ct);
                    process.StandardInput.Close();
                }

                var statusText = await process.StandardOutput.ReadToEndAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (!int.TryParse(statusText.Trim(), out var statusCode))
                {
                    throw new InvalidOperationException(
                        $"curl-impersonate returned unexpected output (exit {process.ExitCode}): {stderr.Trim()}");
                }

                var responseBody = File.Exists(bodyFile) ? await File.ReadAllTextAsync(bodyFile, ct) : "";
                return new AmazonImpersonateResponse((HttpStatusCode)statusCode, responseBody);
            }
            finally
            {
                TryDelete(bodyFile);
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TryDelete(_cookieJarPath);
        _requestLock.Dispose();
    }

    private static string ResolveCurlBinary()
    {
        var configured = Environment.GetEnvironmentVariable("SQUIDWTF_CURL_IMPERSONATE");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return "/usr/local/bin/curl_amz_tls";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}

internal readonly struct AmazonImpersonateResponse(HttpStatusCode statusCode, string body)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}
