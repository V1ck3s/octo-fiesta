using System.Text.Json.Serialization;

namespace octo_fiesta.Models.Tidal;

#region Authentication (auth.tidal.com)

/// <summary>
/// Response of POST /v1/oauth2/device_authorization
/// </summary>
public class TidalDeviceAuthorizationResponse
{
    [JsonPropertyName("deviceCode")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("userCode")]
    public string? UserCode { get; set; }

    [JsonPropertyName("verificationUri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("verificationUriComplete")]
    public string? VerificationUriComplete { get; set; }

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

/// <summary>
/// Successful response of POST /v1/oauth2/token, for both the device code exchange
/// and the refresh token grant. A refresh grant does not return a new refresh token.
/// </summary>
public class TidalTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("user")]
    public TidalTokenUser? User { get; set; }
}

public class TidalTokenUser
{
    [JsonPropertyName("userId")]
    public long UserId { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

/// <summary>
/// Error body of POST /v1/oauth2/token. While the user has not approved the device,
/// Tidal answers HTTP 400 with sub_status 1002.
/// </summary>
public class TidalAuthError
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("sub_status")]
    public int SubStatus { get; set; }
}

#endregion

#region Session and subscription (api.tidal.com)

/// <summary>
/// Response of GET /v1/sessions, used to check that an access token is still valid.
/// </summary>
public class TidalSession
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("userId")]
    public long UserId { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }
}

/// <summary>
/// Response of GET /v1/users/{userId}/subscription
/// </summary>
public class TidalSubscriptionResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("highestSoundQuality")]
    public string? HighestSoundQuality { get; set; }

    [JsonPropertyName("premiumAccess")]
    public bool PremiumAccess { get; set; }

    [JsonPropertyName("subscription")]
    public TidalSubscription? Subscription { get; set; }
}

public class TidalSubscription
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

#endregion

#region Catalogue

/// <summary>
/// Paginated collection returned by search, album tracks, artist albums and playlist items.
/// </summary>
public class TidalItemsResponse<T>
{
    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("totalNumberOfItems")]
    public int TotalNumberOfItems { get; set; }

    [JsonPropertyName("items")]
    public List<T>? Items { get; set; }
}

/// <summary>
/// Response of GET /v1/search. Only the sections matching the requested types are filled.
/// </summary>
public class TidalSearchResponse
{
    [JsonPropertyName("tracks")]
    public TidalItemsResponse<TidalTrack>? Tracks { get; set; }

    [JsonPropertyName("albums")]
    public TidalItemsResponse<TidalAlbum>? Albums { get; set; }

    [JsonPropertyName("artists")]
    public TidalItemsResponse<TidalArtist>? Artists { get; set; }

    [JsonPropertyName("playlists")]
    public TidalItemsResponse<TidalPlaylist>? Playlists { get; set; }
}

/// <summary>
/// Album and playlist item wrapper. The type discriminates tracks from videos.
/// </summary>
public class TidalItemWrapper
{
    [JsonPropertyName("item")]
    public TidalTrack? Item { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class TidalTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("trackNumber")]
    public int TrackNumber { get; set; }

    [JsonPropertyName("volumeNumber")]
    public int VolumeNumber { get; set; }

    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }

    [JsonPropertyName("allowStreaming")]
    public bool AllowStreaming { get; set; } = true;

    [JsonPropertyName("streamReady")]
    public bool StreamReady { get; set; } = true;

    [JsonPropertyName("isrc")]
    public string? Isrc { get; set; }

    [JsonPropertyName("bpm")]
    public int? Bpm { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; set; }

    [JsonPropertyName("artist")]
    public TidalArtist? Artist { get; set; }

    [JsonPropertyName("artists")]
    public List<TidalArtist>? Artists { get; set; }

    [JsonPropertyName("album")]
    public TidalAlbum? Album { get; set; }
}

public class TidalAlbum
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("numberOfTracks")]
    public int NumberOfTracks { get; set; }

    [JsonPropertyName("numberOfVolumes")]
    public int NumberOfVolumes { get; set; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("artist")]
    public TidalArtist? Artist { get; set; }

    [JsonPropertyName("artists")]
    public List<TidalArtist>? Artists { get; set; }
}

public class TidalArtist
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("picture")]
    public string? Picture { get; set; }
}

public class TidalPlaylist
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("numberOfTracks")]
    public int NumberOfTracks { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    /// <summary>
    /// Creation date, kept as text. Tidal writes the offset without a colon
    /// ("2019-12-12T00:00:00.000+0000"), which System.Text.Json refuses to bind to a date.
    /// </summary>
    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("squareImage")]
    public string? SquareImage { get; set; }

    [JsonPropertyName("creator")]
    public TidalCreator? Creator { get; set; }
}

public class TidalCreator
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

#endregion

#region Playback

/// <summary>
/// Response of GET /v1/tracks/{id}/playbackinfopostpaywall.
/// The manifest is base64 and holds either a BTS JSON payload or a DASH MPD document.
/// </summary>
public class TidalPlaybackInfo
{
    [JsonPropertyName("trackId")]
    public long TrackId { get; set; }

    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; set; }

    [JsonPropertyName("assetPresentation")]
    public string? AssetPresentation { get; set; }

    [JsonPropertyName("manifestMimeType")]
    public string? ManifestMimeType { get; set; }

    [JsonPropertyName("manifest")]
    public string? Manifest { get; set; }
}

/// <summary>
/// BTS manifest, base64-decoded from <see cref="TidalPlaybackInfo.Manifest"/>.
/// DASH manifests are parsed into the same shape by <see cref="Services.Common.TidalDashManifestParser"/>.
/// </summary>
public class TidalManifest
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("codecs")]
    public string? Codecs { get; set; }

    [JsonPropertyName("encryptionType")]
    public string? EncryptionType { get; set; }

    /// <summary>
    /// Encrypted security token of an encrypted stream. Null or empty when the stream is in clear.
    /// </summary>
    [JsonPropertyName("keyId")]
    public string? KeyId { get; set; }

    [JsonPropertyName("urls")]
    public List<string>? Urls { get; set; }

    /// <summary>
    /// Total media duration in seconds, parsed from a DASH manifest. Null for BTS manifests.
    /// Used to patch the fMP4 moov duration, see <see cref="Services.Common.Mp4DurationPatcher"/>.
    /// </summary>
    [JsonIgnore]
    public double? DurationSeconds { get; set; }
}

#endregion
