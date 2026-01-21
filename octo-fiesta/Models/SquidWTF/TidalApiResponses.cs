using System.Text.Json.Serialization;

namespace octo_fiesta.Models.SquidWTF;

#region Tidal API Responses (triton.squid.wtf)

/// <summary>
/// Response from /search/ endpoint
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
/// Response from /track/ endpoint (download)
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
/// Response from /info/ endpoint (track metadata)
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

#endregion
