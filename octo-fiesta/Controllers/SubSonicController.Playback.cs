using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Controllers;

public partial class SubsonicController
{
    private IExternalCoverArtService? GetExternalCoverArtService()
        => HttpContext.RequestServices?.GetService<IExternalCoverArtService>();

    /// <summary>
    /// Reports playback state while resolving external song IDs to local IDs.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/reportPlayback")]
    [Route("rest/reportPlayback.view")]
    public async Task<IActionResult> ReportPlaybackState()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

        if (!await TryResolvePlaybackMediaIdAsync(parameters))
        {
            return _responseBuilder.CreateResponse(format, "playbackReport", new { });
        }

        try
        {
            var result = await _proxyService.RelayAsync("rest/reportPlayback", parameters);
            if (IsSubsonicDataNotFound(result.Body, format))
            {
                return _responseBuilder.CreateResponse(format, "playbackReport", new { });
            }

            var contentType = result.ContentType ?? $"application/{format}";
            return File(result.Body, contentType);
        }
        catch (HttpRequestException ex)
        {
            return _responseBuilder.CreateError(format, 0, $"Error connecting to Subsonic server: {ex.Message}");
        }
    }

    private async Task<bool> TryResolvePlaybackMediaIdAsync(Dictionary<string, string> parameters)
    {
        var mediaId = parameters.GetValueOrDefault("mediaId", "");
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            mediaId = parameters.GetValueOrDefault("id", "");
        }

        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return true;
        }

        parameters["mediaId"] = mediaId;

        if (!string.Equals(parameters.GetValueOrDefault("mediaType", "song"), "song", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(mediaId);
        if (!isExternal || !string.Equals(type, "song", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(externalId))
        {
            return true;
        }

        var localId = await _localLibraryService.GetLocalIdForExternalSongAsync(provider, externalId);
        if (string.IsNullOrEmpty(localId))
        {
            return false;
        }

        parameters["mediaId"] = localId;
        return true;
    }

    private Task<IReadOnlyDictionary<string, LocalSongMapping>> GetSearchMappingsAsync() =>
        _localLibraryService.GetMappingsSnapshotAsync(HttpContext.RequestAborted);

    private static bool IsSubsonicDataNotFound(byte[] body, string format)
    {
        if (format == "json")
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("subsonic-response", out var subsonicResponse) &&
                    subsonicResponse.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("code", out var code) &&
                    code.GetInt32() == 70)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }
        else
        {
            var content = Encoding.UTF8.GetString(body);
            if (content.Contains("code=\"70\"", StringComparison.Ordinal) &&
                content.Contains("data not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
