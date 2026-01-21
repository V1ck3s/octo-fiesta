using System.Text.Json.Serialization;

namespace octo_fiesta.Models.SquidWTF;

#region Qobuz API Responses

/// <summary>
/// Response from /api/get-music (search)
/// </summary>
public class QobuzSearchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("data")]
    public QobuzSearchData? Data { get; set; }
}

public class QobuzSearchData
{
    [JsonPropertyName("albums")]
    public QobuzAlbumList? Albums { get; set; }
    
    [JsonPropertyName("tracks")]
    public QobuzTrackList? Tracks { get; set; }
    
    [JsonPropertyName("artists")]
    public QobuzArtistList? Artists { get; set; }
}

public class QobuzAlbumList
{
    [JsonPropertyName("items")]
    public List<QobuzAlbum>? Items { get; set; }
}

public class QobuzTrackList
{
    [JsonPropertyName("items")]
    public List<QobuzTrack>? Items { get; set; }
}

public class QobuzArtistList
{
    [JsonPropertyName("items")]
    public List<QobuzArtist>? Items { get; set; }
}

public class QobuzAlbum
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("artist")]
    public QobuzArtist? Artist { get; set; }
    
    [JsonPropertyName("image")]
    public QobuzImage? Image { get; set; }
    
    [JsonPropertyName("tracks_count")]
    public int TracksCount { get; set; }
    
    [JsonPropertyName("released_at")]
    public long? ReleasedAt { get; set; }
    
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
    
    [JsonPropertyName("tracks")]
    public QobuzTrackList? Tracks { get; set; }
    
    [JsonPropertyName("genre")]
    public QobuzGenre? Genre { get; set; }
}

public class QobuzTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
    
    [JsonPropertyName("track_number")]
    public int TrackNumber { get; set; }
    
    [JsonPropertyName("album")]
    public QobuzAlbum? Album { get; set; }
    
    [JsonPropertyName("performer")]
    public QobuzArtist? Performer { get; set; }
    
    [JsonPropertyName("parental_warning")]
    public bool ParentalWarning { get; set; }
}

public class QobuzArtist
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("image")]
    public QobuzImage? Image { get; set; }
    
    [JsonPropertyName("albums_count")]
    public int AlbumsCount { get; set; }
}

public class QobuzImage
{
    [JsonPropertyName("small")]
    public string? Small { get; set; }
    
    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }
    
    [JsonPropertyName("large")]
    public string? Large { get; set; }
}

public class QobuzGenre
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// Response from /api/get-album
/// </summary>
public class QobuzAlbumResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("data")]
    public QobuzAlbum? Data { get; set; }
}

/// <summary>
/// Response from /api/get-artist
/// </summary>
public class QobuzArtistResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("data")]
    public QobuzArtistData? Data { get; set; }
}

public class QobuzArtistData
{
    [JsonPropertyName("artist")]
    public QobuzArtist? Artist { get; set; }
    
    [JsonPropertyName("albums")]
    public QobuzAlbumList? Albums { get; set; }
}

/// <summary>
/// Response from /api/download-music
/// </summary>
public class QobuzDownloadResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("data")]
    public QobuzDownloadData? Data { get; set; }
}

public class QobuzDownloadData
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

#endregion
