
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace octo_fiesta.Models.Yandex;

/// <summary>
/// Common Yandex Music API response wrapper.
/// May contain either actual response or an error object.
/// </summary>
/// <typeparam name="T">Type of actual response payload</typeparam>
public class YandexResponse<T> where T: class
{
    [JsonPropertyName("error")]
    public YandexResponseError? Error { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }
}

/// <summary>
/// Top level response error.
/// </summary>
public class YandexResponseError
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Representation of a track in Yandex Music API.
/// Used in all places where tracks appear.
/// </summary>
public class YandexTrack
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Content warning. Known values: 'explicit', 'clean'
    /// </summary>
    [JsonPropertyName("contentWarning")]
    public string? ContentWarning { get; set; }

    /// <summary>
    /// Is tracks available for listening
    /// </summary>
    [JsonPropertyName("available")]
    public bool? Available { get; set; }

    /// <summary>
    /// Disclaimers may contain another 'explicit' tag.
    /// </summary>
    [JsonPropertyName("disclaimers")]
    public List<string>? Disclaimers { get; set; } = new();

    [JsonPropertyName("durationMs")]
    public int? DurationMs { get; set; }

    [JsonPropertyName("coverUri")]
    public string? CoverUri { get; set; }

    /// <summary>
    /// Fallback uri for cover images.
    /// </summary>
    [JsonPropertyName("ogImage")]
    public string? OgImage { get; set; }
    
    [JsonPropertyName("artists")]
    public List<YandexArtistShort>? Artists { get; set; } = new();

    [JsonPropertyName("albums")]
    public List<YandexTrackAlbum>? Albums { get; set; } = new();
}

/// <summary>
/// Album details included in Track.
/// Contains necessary details for tagging such as track position in album
/// and total tracks count.
/// </summary>
public class YandexTrackAlbum
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("trackPosition")]
    public YandexTrackPosition? TrackPosition { get; set; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; set; } = 0;

    [JsonPropertyName("artists")]
    public List<YandexArtistShort> Artists { get; set; } = new();

    [JsonPropertyName("labels")]
    public List<YandexLabel> Labels { get; set; } = new();
}

/// <summary>
/// Disc number and track number of a Track inside in an Album.
/// </summary>
public class YandexTrackPosition
{
    [JsonPropertyName("volume")]
    public int? Volume { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }
}

/// <summary>
/// Label name.
/// </summary>
[JsonConverter(typeof(YandexLabelConverter))]
public class YandexLabel
{
    public string? Name { get; set; }
}

/// <summary>
/// Short version of Artist payload. Appers inside of Tracks and Albums.
/// </summary>
public class YandexArtistShort
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Full version of Artist payload from /artists/ endpoint.
/// </summary>
public class YandexArtist
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("cover")]
    public YandexCover? Cover { get; set; }
    /// <summary>
    /// Fallback uri for cover images.
    /// </summary>
    [JsonPropertyName("ogImage")]
    public string? OgImage { get; set; }

    [JsonPropertyName("counts")]
    public YandexArtistCounts? Counts { get; set; }
}

/// <summary>
/// Actual YandexArtist object from /artists/ endpoint
/// is wrapped by this wrapper.
/// </summary>
public class YandexArtistWrapper
{
    [JsonPropertyName("artist")]
    public required YandexArtist Artist { get; set; }
}

/// <summary>
/// Counts of different types of media produced by Artist.
/// </summary>
public class YandexArtistCounts
{
    [JsonPropertyName("directAlbums")]
    public int DirectAlbums { get; set; } = 0;
}

/// <summary>
/// General type of Cover object included in Tracks, Albums and Artists.
/// </summary>
public class YandexCover
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

/// <summary>
/// Full version of Album response containing Album Tracks.
/// /albums/{id}/with-tracks
/// </summary>
public class YandexAlbumWithTracks
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("artists")]
    public List<YandexArtistShort>? Artists { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("trackCount")]
    public int? TrackCount { get; set; }

    [JsonPropertyName("coverUri")]
    public string? CoverUri { get; set; }

    [JsonPropertyName("cover")]
    public YandexCover? Cover { get; set; }

    /// <summary>
    /// Fallback uri for cover images.
    /// </summary>
    [JsonPropertyName("ogImage")]
    public string? OgImage { get; set; }

    [JsonPropertyName("genre")]
    public string? Genre { get; set; }
    
    /// <summary>
    /// Nested list representing disks with tracks.
    /// </summary>
    [JsonPropertyName("volumes")]
    public  List<List<YandexTrack>>? Volumes { get; set; }
}

/// <summary>
/// List of Albums made by Artist. 
/// /artist/{id}/direct-albums
/// </summary>
public class YandexArtistDirectAlbums
{
    [JsonPropertyName("albums")]
    public List<YandexAlbumId>? Albums { get; set; }
}

/// <summary>
/// Special short version of Album for places where you can't obtain full Album
/// and where having only Album ID is enough.
/// </summary>
public class YandexAlbumId
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

/// <summary>
/// Search results response from /search endpoint.
/// </summary>
public class YandexSearchResults
{
    [JsonPropertyName("tracks")]
    public  YandexSearchResult<YandexTrack>? Tracks { get; set; }

    [JsonPropertyName("artists")]
    public YandexSearchResult<YandexArtist>? Artists { get; set; }

    [JsonPropertyName("albums")]
    public YandexSearchResult<YandexAlbumId>? Albums { get; set; }
    
    [JsonPropertyName("playlists")]
    public YandexSearchResult<YandexPlaylist>? Playlists { get; set; }

    [JsonPropertyName("best")]
    public YandexSearchBestResult? Best { get; set; }
}

/// <summary>
/// Common wrapper for different types of search results.
/// </summary>
/// <typeparam name="T">Type of result. Track, Artist, Album or Playlist.</typeparam>
public class YandexSearchResult<T>
{
    [JsonPropertyName("results")]
    public List<T>? Results { get; set; }

    [JsonPropertyName("perPage")]
    public int PerPage { get; set; }
}

/// <summary>
/// Playlist response or search result payload.
/// </summary>
public class YandexPlaylist
{
    [JsonPropertyName("playlistUuid")]
    public required string Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("owner")]
    public YandexPlaylistOwner? Owner { get; set; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; set; }

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; }

    [JsonPropertyName("created")]
    public  string? CreatedAt { get; set; }

    [JsonPropertyName("ogImage")]
    public string? OgImage { get; set; }

    [JsonPropertyName("cover")]
    public YandexPlaylistCover? Cover { get; set; }

}

/// <summary>
/// Simple model for /playlist endpoint response containing only list if Tracks.
/// </summary>
public class YandexPlaylistTracks
{
    [JsonPropertyName("tracks")]
    public List<YandexPlaylistTrackWrapper> Tracks { get; set; } = new();

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public class YandexPlaylistTrackWrapper
{
    [JsonPropertyName("track")]
    public required YandexTrack Track { get; set; }

    [JsonPropertyName("originalIndex")]
    public int Index { get; set; }
}

/// <summary>
/// Special type of cover. May contains multiple images inside.
/// </summary>
public class YandexPlaylistCover : YandexCover
{
    [JsonPropertyName("itemsUri")]
    public  List<string>? ItemsUri { get; set; }
}

/// <summary>
/// Owner of playlist. Maps to Subsonic Curator Name.
/// </summary>
public class YandexPlaylistOwner
{
    [JsonPropertyName("login")]
    public required string Login { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}


/// <summary>
/// Yandex provides single "Best Result" in their Search API responses.
/// It can be on of: Track, Album, Artist
/// This class holds JsonElement for the result and tries to provide "Best Result"
/// with appropriate type based on "type" field value.
/// </summary>
public class YandexSearchBestResult
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("result")]
    public required JsonElement Result { get; set; }

    [JsonIgnore]
    public YandexTrack? Track 
    {
        get => Deserialize<YandexTrack>();
    }

    [JsonIgnore]
    public YandexAlbumId? Album
    {
        get => Deserialize<YandexAlbumId>();
    }
    
    [JsonIgnore]
    public YandexArtist? Artist
    {
        get => Deserialize<YandexArtist>();
    }

    private T? Deserialize<T>() where T: class
    {
        JsonSerializerOptions jsonSerializerOptions = new()
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString    
        };
        try
        {
            return Result.Deserialize<T>(jsonSerializerOptions);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

}

public class YandexDownloadInfoWrapper
{
    [JsonPropertyName("name")]
    public string? ErrorName { get; set; }

    [JsonPropertyName("message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("downloadInfo")]
    public YandexDownloadInfo? DownloadInfo { get; set; }
}

public class YandexDownloadInfo
{
    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }

    [JsonPropertyName("codec")]
    public required string Codec { get; set; }

    [JsonPropertyName("quality")]
    public required string Quality { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("urls")]
    public List<string> Urls { get; set; } = new();

    [JsonPropertyName("key")]
    public required string Key { get; set; }
}


/// <summary>
/// /tracks/{id}/download-info returns a list of download options.
/// This class represents a single option in that list.
/// This is used in legacy method of downloading. 
/// </summary>
public class YandexDownloadOptionLegacy
{
    [JsonPropertyName("downloadInfoUrl")]
    public required string Url { get; set; }

    [JsonPropertyName("bitrateInKbps")]
    public required int BitRate { get; set; }

    [JsonPropertyName("codec")]
    public required string Codec { get; set; }
}

/// <summary>
/// Details required to build song download URI.
/// Obtained from an URL provided by YandexTrackDownloadOptionLegacy
/// This is used in legacy method of downloading
/// </summary>
[XmlRoot("download-info")]
public class YandexDownloadInfoLegacy
{
    [XmlElement("host")]
    public required string Host { get; set; }
    [XmlElement("path")]
    public required string Path { get; set; }
    [XmlElement("ts")]
    public required string Ts { get; set; }
    [XmlElement("s")]
    public required string S { get; set; }
}

public class YandexUserAccountStatus
{
    [JsonPropertyName("plus")]
    public YandexPlusStatus? PlusStatus { get; set; }
}

public class YandexPlusStatus
{
    [JsonPropertyName("hasPlus")]
    public bool HasPlus { get; set; }
}

/// <summary>
/// JSON Converter for YandexLabel.
/// Sometimes label is just a string. Sometimes it is an object with a 'name' key.
/// </summary>
public class YandexLabelConverter : JsonConverter<YandexLabel>
{
    public override YandexLabel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new YandexLabel { Name = reader.GetString() };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            return new YandexLabel
            {
                Name = root.GetProperty("name").GetString()
            };
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, YandexLabel value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}
