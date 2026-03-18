using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Models.Settings;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Yandex;
using System.Xml.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Engines;
using Microsoft.AspNetCore.Http.Extensions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.IO;

namespace octo_fiesta.Services.Yandex;

public class YandexDownloadService : BaseDownloadService
{
    private readonly string? _oauthToken;
    private readonly string _preferredQuality;
    private readonly HttpClient _httpClient;
    private readonly ILogger<YandexDownloadService> _logger;
    private const string MD_SIGNING_SALT = "XGRlBW9FXlekgbPrRHuSiA";
    private readonly byte[] HMAC_SECRET = Encoding.UTF8.GetBytes("kzqU4XhfCaY6B6JTHODeq5");
    private const string AlbumPrefix = "ext-yandex-album-";


    public YandexDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<YandexSettings> yandexSettings,
        IServiceProvider serviceProvider,
        ILogger<YandexDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings.Value, serviceProvider, logger)
    {   
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Yandex");

        var yaSettings = yandexSettings.Value;
        _preferredQuality = string.IsNullOrWhiteSpace(yaSettings.Quality) ? "FLAC" : yaSettings.Quality;
        _oauthToken = yaSettings.OAuthToken;
    }
    
    #region Interface implementation

    protected override string ProviderName => "yandex";

    public override async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrWhiteSpace(_oauthToken))
        {
            _logger.LogWarning("No user OAuth token specified for provider Yandex.");
            return false;
        }
        string accountStatusUrl = "/account/status";
        var response = await _httpClient.GetAsync(accountStatusUrl);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Couldn't get user account status for provider Yandex. "
                + "Got status code {StatusCode} for request {Url}",
                response.StatusCode, accountStatusUrl
            );
            return false;
        }
        string responseContent = await response.Content.ReadAsStringAsync();
        
        // Playback and downloads work correctly only with Yandex Plus subscription
        // So we check whether user has one
        YandexResponse<YandexUserAccountStatus>? accountStatusResponse = JsonSerializer.Deserialize<YandexResponse<YandexUserAccountStatus>>(responseContent);
        if (accountStatusResponse?.Result?.PlusStatus?.HasPlus != true)
        {
            _logger.LogWarning(
                "User has no active Yandex Plus subscription. Please renew your subscription."
            );
            return false;
        }
        return true;
    }

    protected override async Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        
        DownloadResult? downloadResult = await DownloadTrackModernAsync(trackId, song, cancellationToken);
        if (downloadResult is not null)
        {
            return downloadResult;
        }
        _logger.LogWarning(
            "Track '{TrackId}: {Title}' Downloading using modern API failed. Trying legacy API.", trackId, song.Title
        );


        downloadResult = await DownloadTrackLegacyAsync(trackId, song, cancellationToken);
        if (downloadResult is not null)
        {
            return downloadResult;
        }
        throw new Exception(
            $"Track  '{trackId}: {song.Title}' downloading failed using both modern and legacy APIs."
        );
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        if (albumId.StartsWith(AlbumPrefix))
        {
            return albumId[AlbumPrefix.Length..];
        }
        return null;
    }

    protected override string? GetTargetQuality() => _preferredQuality;

    #endregion

    #region Modern Yandex API
    
    private async Task<DownloadResult?> DownloadTrackModernAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Downloading track {TrackId} with encrypted Yandex API", trackId);
        
        YandexDownloadInfo? downloadInfo = await GetYandexDownloadInfoModernAsync(trackId, cancellationToken);
        if (downloadInfo is null)
        {
            _logger.LogWarning(
                "Failed to get download info for track {TrackId} with encrypted Yandex API.",
                trackId
            );
            return null;
        }

        Stream? downloadStream = await GetDecryptedDownloadStreamModernAsync(downloadInfo!, cancellationToken);
        if (downloadStream is null)
        {
            _logger.LogWarning("Failed to get decrypted stream for track {TrackId}", trackId);
            return null;
        }

        string extension =  YandexQuality.CodecToExtension(downloadInfo.Codec);
        string actualQuality = YandexQuality.FromApiParams(downloadInfo!.Codec, downloadInfo.Bitrate);

        return new DownloadResult(downloadStream, extension, actualQuality);
    }

    private async Task<YandexDownloadInfo?> GetYandexDownloadInfoModernAsync(string trackId, CancellationToken cancellationToken)
    {
        // Craft a signed request for download info
        var (apiQualityName,codecs) = YandexQuality.ToApiParams(_preferredQuality);
        string transports = "encraw";
        long timeStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        byte[] bytesToSign = Encoding.UTF8.GetBytes($"{timeStamp}{trackId}{apiQualityName}{string.Join("", codecs)}{transports}");
        byte[] hash = HMACSHA256.HashData(HMAC_SECRET, bytesToSign);
        string sign = Convert.ToBase64String(hash)[..^1];

        var queryBuilder = new QueryBuilder
        {
            { "ts", timeStamp.ToString() },
            { "trackId", trackId },
            { "quality", apiQualityName},
            { "codecs", string.Join(",", codecs) },
            { "transports", transports },
            { "sign", sign }
        };
        string url = $"/get-file-info/{queryBuilder}";
        
        // Get download info with a direct download link and a decryption key
        using var downloadInfoResponse = await _httpClient.GetAsync(url, cancellationToken);
        if (!downloadInfoResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Yandex API returned status code {StatusCode} for track {TrackId} download info request {Url}.",
                downloadInfoResponse.StatusCode, trackId, url
            );
            return null;
        }

        string downloadInfoString = await downloadInfoResponse.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrEmpty(downloadInfoString))
        {
            _logger.LogWarning("Yandex API returned empty response for track {TrackId} download info request", trackId);
            return null;
        }

        var downloadInfoWrapper = JsonSerializer.Deserialize<YandexResponse<YandexDownloadInfoWrapper>>(downloadInfoString);
        if(downloadInfoWrapper is null)
        {
            _logger.LogWarning("Unable to parse Yandex API response for track {TrackId} download info.", trackId);
            return null;
        }

        string? apiErrorCode = null;
        string? apiErrorMessage = null;
        if (downloadInfoWrapper.Error is not null)
        {
            apiErrorCode = downloadInfoWrapper.Error.Name;
            apiErrorMessage = downloadInfoWrapper.Error.Message;
        }
        else if (downloadInfoWrapper.Result?.ErrorName is not null)
        {
            apiErrorCode = downloadInfoWrapper.Result.ErrorName;
            apiErrorMessage = downloadInfoWrapper.Result.ErrorMessage;
        }

        if (apiErrorCode is not null)
        {
            _logger.LogWarning(
                "Yandex API returned Error '{ErrorCode}' with message '{ErrorMessage}' for track {TrackId} download info request.",
                apiErrorCode, apiErrorMessage, trackId
            );
            return null;
        }

        return downloadInfoWrapper.Result!.DownloadInfo!;
    }

    private async Task<Stream?> GetDecryptedDownloadStreamModernAsync(YandexDownloadInfo downloadInfo, CancellationToken cancellationToken)
    {
        List<string> urls = [downloadInfo.Url];
        urls.AddRange(downloadInfo.Urls);

        // try multiple provided download urls until successful response
        foreach (string directUrl in urls)
        {
            var response = await _httpClient.GetAsync(directUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var encryptedStream = await HttpResponseStream.CreateAsync(response, cancellationToken);

                // initialize decryption machinery
                byte[] keyHex = Convert.FromHexString(downloadInfo.Key);
                byte[] counter = new byte[16];
                IBlockCipher aes  = new AesEngine();
                var bufferedCipher = new BufferedBlockCipher(new SicBlockCipher(aes));
                var parameters = new ParametersWithIV(new KeyParameter(keyHex), counter);
                bufferedCipher.Init(false, parameters);
                
                return new CipherStream(encryptedStream, bufferedCipher, null);
            }

            _logger.LogWarning(
                "Yandex API returned status code {StatusCode} for encrypted track download request",
                response.StatusCode
            );
        }

        _logger.LogWarning("Unable to download track by any of provided urls.");
        return null;
    }

    #endregion

    #region Legacy Yandex API

    private async Task<DownloadResult?> DownloadTrackLegacyAsync(string trackId, Song song, CancellationToken cancellationToken)
    {
         _logger.LogInformation("Downloading track {TrackId} with legacy Yandex API", trackId);

        YandexDownloadOptionLegacy? downloadOption = await GetDownloadOptionLegacyAsync(trackId, cancellationToken);
        if (downloadOption is null)
        {
            _logger.LogWarning("Failed to get download option for track {TrackId}", trackId);
            return null;
        }

        string? directUrl = await GetDirectDownloadUrlLegacyAsync(downloadOption, cancellationToken);
        if (directUrl is null)
        {
            _logger.LogWarning("Failed to get direct download URL for track {TrackId}", trackId);
            return null;
        }
        
        // Start download
        var downloadResponse = await _httpClient.GetAsync(directUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Yandex API returned status code {StatusCode} for track {TrackId} download request",
                downloadResponse.StatusCode, trackId
            );
            return null;
        }

        Stream downloadStream = await HttpResponseStream.CreateAsync(downloadResponse, cancellationToken);
        string extension = YandexQuality.CodecToExtension(downloadOption.Codec);
        string actualQuality = YandexQuality.FromApiParams(downloadOption.Codec, downloadOption.BitRate);

        return new DownloadResult(
            downloadStream,
            extension,
            actualQuality
        );
    }

    private async Task<YandexDownloadOptionLegacy?> GetDownloadOptionLegacyAsync(string trackId, CancellationToken cancellationToken)
    {
        // Get a list of available download options
        using var downloadOptionsResponse = await _httpClient.GetAsync($"/tracks/{trackId}/download-info", cancellationToken);
        if (!downloadOptionsResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Yandex API returned status code {StatusCode} for track {TrackId} download options.",
                downloadOptionsResponse.StatusCode, trackId
            );
            return null;
        }
        string downloadOptionsString = await downloadOptionsResponse.Content.ReadAsStringAsync(cancellationToken);        
        var downloadOptions = JsonSerializer.Deserialize<YandexResponse<List<YandexDownloadOptionLegacy>>>(downloadOptionsString);
        if (downloadOptions is null)
        {
            _logger.LogWarning("Couldn't parse Yandex API response for track {TrackId} download options.", trackId);
            return null;
        }
        if (downloadOptions.Error is not null)
        {
            var error = downloadOptions.Error;
            _logger.LogWarning(
                "Yandex API returned an error ({ErrorName}: {ErrorMessage}) for track {TrackId} download options.",
                error.Name, error.Message, trackId
            );
            return null;
        }
        
        List<YandexDownloadOptionLegacy> options = downloadOptions.Result!;
        if (options.Count == 0)
        {
            _logger.LogWarning("Yandex API returned no download options for track {TrackId}.", trackId);
            return null;
        }

        // Select best suitable quality for a download
        // Yandex Legacy API usually returns up to two options: MP3 192 and when available MP3 320
        if (options.Count == 1) return options[0];

        string preferredQualityLevel = YandexQuality.ToApiParams(_preferredQuality).level;
        return preferredQualityLevel == "lossless"
            ? options.MaxBy(option => option.BitRate)!
            : options.MinBy(option => option.BitRate)!;
    }

    private async Task<string?> GetDirectDownloadUrlLegacyAsync(YandexDownloadOptionLegacy downloadOption, CancellationToken cancellationToken)
    {
        // Fetch an actual download info to build a direct download link
        using var downloadInfoResponse = await _httpClient.GetAsync(downloadOption.Url, cancellationToken);
        if (!downloadInfoResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Yandex Legacy API returned status code {StatusCode} for track download info request {Url}.",
                downloadInfoResponse.StatusCode, downloadOption.Url
            );
            return null;
        }

        string downloadInfoResponseString = await downloadInfoResponse.Content.ReadAsStringAsync(cancellationToken);
        var xmlSerializer = new XmlSerializer(typeof(YandexDownloadInfoLegacy));
        using var reader = new StringReader(downloadInfoResponseString);
        var downloadInfo = (YandexDownloadInfoLegacy?)xmlSerializer.Deserialize(reader);
        if (downloadInfo is null)
        {
            _logger.LogWarning("Failed to deserialize Yandex API response for track download info request.");
            return null;
        };


        // Prepare signed download URL
        string stringToSign = MD_SIGNING_SALT + downloadInfo.Path[1..] + downloadInfo.S;
        string sign = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(stringToSign)));
        return $"https://{downloadInfo.Host}/get-mp3/{sign}/{downloadInfo.Ts}{downloadInfo.Path}";
    }

    #endregion

}