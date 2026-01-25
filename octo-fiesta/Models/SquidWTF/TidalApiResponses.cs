using System.Text.Json.Serialization;

namespace octo_fiesta.Models.SquidWTF;

#region Tidal API Responses (triton.squid.wtf)

/// <summary>
/// Generic wrapper for Tidal API responses with data.items structure (used for track search)
/// </summary>
public class TidalDataResponse<T>
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    [JsonPropertyName("data")]
    public TidalDataWrapper<T>? Data { get; set; }
}

public class TidalDataWrapper<T>
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
/// Response structure for album/artist/playlist searches which have nested type wrappers
/// </summary>
public class TidalNestedSearchResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    [JsonPropertyName("data")]
    public TidalNestedSearchData? Data { get; set; }
}

public class TidalNestedSearchData
{
    [JsonPropertyName("artists")]
    public TidalDataWrapper<TidalArtist>? Artists { get; set; }
    
    [JsonPropertyName("albums")]
    public TidalDataWrapper<TidalAlbum>? Albums { get; set; }
    
    [JsonPropertyName("playlists")]
    public TidalDataWrapper<TidalPlaylist>? Playlists { get; set; }
}

/// <summary>
/// Response from /search/ endpoint (legacy format - kept for compatibility)
/// </summary>
public class TidalSearchResponse
{
    [JsonPropertyName("tracks")]
    public List<TidalTrack>? Tracks { get; set; }
    
    [JsonPropertyName("albums")]
    public List<TidalAlbum>? Albums { get; set; }
    
    [JsonPropertyName("artists")]
    public List<TidalArtist>? Artists { get; set; }
    
    [JsonPropertyName("playlists")]
    public List<TidalPlaylist>? Playlists { get; set; }
}

public class TidalTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
    
    [JsonPropertyName("trackNumber")]
    public int TrackNumber { get; set; }
    
    [JsonPropertyName("volumeNumber")]
    public int VolumeNumber { get; set; }
    
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    
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
    
    [JsonPropertyName("artist")]
    public TidalArtist? Artist { get; set; }
    
    [JsonPropertyName("artists")]
    public List<TidalArtist>? Artists { get; set; }
    
    [JsonPropertyName("tracks")]
    public List<TidalTrack>? Tracks { get; set; }
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
    
    [JsonPropertyName("numberOfTracks")]
    public int NumberOfTracks { get; set; }
    
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
    
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

/// <summary>
/// Response from /playlist/ endpoint - single playlist with tracks
/// </summary>
public class TidalPlaylistResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    [JsonPropertyName("playlist")]
    public TidalPlaylist? Playlist { get; set; }
    
    [JsonPropertyName("items")]
    public List<TidalPlaylistItem>? Items { get; set; }
}

/// <summary>
/// Wrapper for items in a playlist response
/// </summary>
public class TidalPlaylistItem
{
    [JsonPropertyName("item")]
    public TidalTrack? Item { get; set; }
    
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Wrapper for /track/ endpoint response (download)
/// The API returns { "version": "...", "data": { ... track download info ... } }
/// </summary>
public class TidalTrackDownloadResponseWrapper
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    [JsonPropertyName("data")]
    public TidalTrackResponse? Data { get; set; }
}

/// <summary>
/// Response from /track/ endpoint (download) - the data payload
/// </summary>
public class TidalTrackResponse
{
    [JsonPropertyName("trackId")]
    public long TrackId { get; set; }
    
    [JsonPropertyName("assetPresentation")]
    public string? AssetPresentation { get; set; }
    
    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; set; }
    
    [JsonPropertyName("manifest")]
    public string? Manifest { get; set; }
    
    [JsonPropertyName("manifestMimeType")]
    public string? ManifestMimeType { get; set; }
}

/// <summary>
/// Decoded manifest content (base64 decoded from TidalTrackResponse.Manifest)
/// </summary>
public class TidalManifest
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }
    
    [JsonPropertyName("codecs")]
    public string? Codecs { get; set; }
    
    [JsonPropertyName("urls")]
    public List<string>? Urls { get; set; }
}

/// <summary>
/// Wrapper for /info/ endpoint response (track metadata)
/// The API returns { "version": "...", "data": { ... track info ... } }
/// </summary>
public class TidalTrackInfoResponseWrapper
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    [JsonPropertyName("data")]
    public TidalTrackInfoResponse? Data { get; set; }
}

/// <summary>
/// Track info data from /info/ endpoint
/// </summary>
public class TidalTrackInfoResponse
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
    
    [JsonPropertyName("trackNumber")]
    public int TrackNumber { get; set; }
    
    [JsonPropertyName("volumeNumber")]
    public int VolumeNumber { get; set; }
    
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    
    [JsonPropertyName("isrc")]
    public string? Isrc { get; set; }
    
    [JsonPropertyName("artist")]
    public TidalArtist? Artist { get; set; }
    
    [JsonPropertyName("artists")]
    public List<TidalArtist>? Artists { get; set; }
    
    [JsonPropertyName("album")]
    public TidalAlbum? Album { get; set; }
}

/// <summary>
/// Response from /album/?id= endpoint - single album with tracks
/// </summary>
public class TidalAlbumResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    [JsonPropertyName("data")]
    public TidalAlbumData? Data { get; set; }
}

/// <summary>
/// Album data from /album/ endpoint (includes tracks as items)
/// </summary>
public class TidalAlbumData
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
    
    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }
    
    [JsonPropertyName("artist")]
    public TidalArtist? Artist { get; set; }
    
    [JsonPropertyName("artists")]
    public List<TidalArtist>? Artists { get; set; }
    
    [JsonPropertyName("items")]
    public List<TidalAlbumItem>? Items { get; set; }
}

/// <summary>
/// Wrapper for items in an album response (tracks wrapped in item/type structure)
/// </summary>
public class TidalAlbumItem
{
    [JsonPropertyName("item")]
    public TidalTrack? Item { get; set; }
    
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Response from /artist/?id= endpoint - artist info with cover
/// </summary>
public class TidalArtistResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    
    [JsonPropertyName("artist")]
    public TidalArtistData? Artist { get; set; }
    
    [JsonPropertyName("cover")]
    public TidalArtistCover? Cover { get; set; }
}

/// <summary>
/// Extended artist data from /artist/ endpoint
/// </summary>
public class TidalArtistData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("picture")]
    public string? Picture { get; set; }
    
    [JsonPropertyName("popularity")]
    public int Popularity { get; set; }
    
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    [JsonPropertyName("artistTypes")]
    public List<string>? ArtistTypes { get; set; }
}

/// <summary>
/// Artist cover image info from /artist/ endpoint
/// </summary>
public class TidalArtistCover
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("750")]
    public string? Image750 { get; set; }
}

#endregion
