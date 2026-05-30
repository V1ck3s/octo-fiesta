using System.Text.Json;
using System.Text.Json.Serialization;

namespace octo_fiesta.Models.Yandex;

/// <summary>
/// Common Yandex Music API response wrapper.
/// May contain either actual response or an error object.
/// </summary>
/// <typeparam name="T">Type of actual response payload</typeparam>
public record YandexResponse<T> where T: class
{
    [JsonPropertyName("error")]
    public YandexResponseError? Error { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }
}

/// <summary>
/// Top level response error.
/// </summary>
public record YandexResponseError
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// Representation of a track in Yandex Music API.
/// Used in all places where tracks appear.
/// </summary>
public record YandexTrack
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// Content warning. Known values: 'explicit', 'clean'
    /// </summary>
    [JsonPropertyName("contentWarning")]
    public string? ContentWarning { get; init; }

    /// <summary>
    /// Is tracks available for listening
    /// </summary>
    [JsonPropertyName("available")]
    public bool? Available { get; init; }

    /// <summary>
    /// Disclaimers may contain another 'explicit' tag.
    /// </summary>
    [JsonPropertyName("disclaimers")]
    public List<string>? Disclaimers { get; init; } = new();

    [JsonPropertyName("durationMs")]
    public int? DurationMs { get; init; }

    [JsonPropertyName("coverUri")]
    public string? CoverUri { get; init; }

    /// <summary>
    /// Fallback uri for cover images.
    /// </summary>
    [JsonPropertyName("ogImage")]
    public string? OgImage { get; init; }
    
    [JsonPropertyName("artists")]
    public List<YandexArtistShort>? Artists { get; init; } = new();

    [JsonPropertyName("albums")]
    public List<YandexTrackAlbum>? Albums { get; init; } = new();
}

/// <summary>
/// Album details included in Track.
/// Contains necessary details for tagging such as track position in album
/// and total tracks count.
/// </summary>
public record YandexTrackAlbum
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("trackPosition")]
    public YandexTrackPosition? TrackPosition { get; init; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; init; } = 0;

    [JsonPropertyName("artists")]
    public List<YandexArtistShort> Artists { get; init; } = new();

    [JsonPropertyName("labels")]
    public List<YandexLabel> Labels { get; init; } = new();
}

/// <summary>
/// Disc number and track number of a Track inside in an Album.
/// </summary>
public record YandexTrackPosition
{
    [JsonPropertyName("volume")]
    public int? Volume { get; init; }

    [JsonPropertyName("index")]
    public int? Index { get; init; }
}

/// <summary>
/// Label name.
/// </summary>
[JsonConverter(typeof(YandexLabelConverter))]
public record YandexLabel
{
    public string? Name { get; init; }
}

/// <summary>
/// Short version of Artist payload. Appers inside of Tracks and Albums.
/// </summary>
public record YandexArtistShort
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Full version of Artist payload from /artists/ endpoint.
/// </summary>
public record YandexArtist
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("cover")]
    public YandexCover? Cover { get; init; }
    /// <summary>
    /// Fallback uri for cover images.
    /// </summary>
    [JsonPropertyName("ogImage")]
    public string? OgImage { get; init; }

    [JsonPropertyName("counts")]
    public YandexArtistCounts? Counts { get; init; }
}

/// <summary>
/// Actual YandexArtist object from /artists/ endpoint
/// is wrapped by this wrapper.
/// </summary>
public record YandexArtistWrapper
{
    [JsonPropertyName("artist")]
    public required YandexArtist Artist { get; init; }
}

/// <summary>
/// Counts of different types of media produced by Artist.
/// </summary>
public record YandexArtistCounts
{
    [JsonPropertyName("directAlbums")]
    public int DirectAlbums { get; init; } = 0;
}

/// <summary>
/// General type of Cover object included in Tracks, Albums and Artists.
/// </summary>
public record YandexCover
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

/// <summary>
/// Full version of Album response containing Album Tracks.
/// /albums/{id}/with-tracks
/// </summary>
public record YandexAlbumWithTracks
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("artists")]
    public List<YandexArtistShort>? Artists { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("trackCount")]
    public int? TrackCount { get; init; }

    [JsonPropertyName("coverUri")]
    public string? CoverUri { get; init; }

    [JsonPropertyName("cover")]
    public YandexCover? Cover { get; init; }

    /// <summary>
    /// Fallback uri for cover images.
    /// </summary>
    [JsonPropertyName("ogImage")]
    public string? OgImage { get; init; }

    [JsonPropertyName("genre")]
    public string? Genre { get; init; }
    
    /// <summary>
    /// Nested list representing disks with tracks.
    /// </summary>
    [JsonPropertyName("volumes")]
    public  List<List<YandexTrack>>? Volumes { get; init; }
}

/// <summary>
/// List of Albums made by Artist. 
/// /artist/{id}/direct-albums
/// </summary>
public record YandexArtistDirectAlbums
{
    [JsonPropertyName("albums")]
    public List<YandexAlbumId>? Albums { get; init; }
}

/// <summary>
/// Special short version of Album for places where you can't obtain full Album
/// and where having only Album ID is enough.
/// </summary>
public record YandexAlbumId
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

/// <summary>
/// Search results response from /search endpoint.
/// </summary>
public record YandexSearchResults
{
    [JsonPropertyName("tracks")]
    public  YandexSearchResult<YandexTrack>? Tracks { get; init; }

    [JsonPropertyName("artists")]
    public YandexSearchResult<YandexArtist>? Artists { get; init; }

    [JsonPropertyName("albums")]
    public YandexSearchResult<YandexAlbumId>? Albums { get; init; }
    
    [JsonPropertyName("playlists")]
    public YandexSearchResult<YandexPlaylist>? Playlists { get; init; }

    [JsonPropertyName("best")]
    public YandexSearchBestResult? Best { get; init; }
}

/// <summary>
/// Common wrapper for different types of search results.
/// </summary>
/// <typeparam name="T">Type of result. Track, Artist, Album or Playlist.</typeparam>
public record YandexSearchResult<T>
{
    [JsonPropertyName("results")]
    public List<T>? Results { get; init; }

    [JsonPropertyName("perPage")]
    public int PerPage { get; init; }
}

/// <summary>
/// Playlist response or search result payload.
/// </summary>
public record YandexPlaylist
{
    [JsonPropertyName("playlistUuid")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("owner")]
    public YandexPlaylistOwner? Owner { get; init; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; init; }

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; init; }

    [JsonPropertyName("created")]
    public  string? CreatedAt { get; init; }

    [JsonPropertyName("ogImage")]
    public string? OgImage { get; init; }

    [JsonPropertyName("cover")]
    public YandexPlaylistCover? Cover { get; init; }

}

/// <summary>
/// Simple model for /playlist endpoint response containing only list if Tracks.
/// </summary>
public record YandexPlaylistTracks
{
    [JsonPropertyName("tracks")]
    public List<YandexPlaylistTrackWrapper> Tracks { get; init; } = new();

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

public record YandexPlaylistTrackWrapper
{
    [JsonPropertyName("track")]
    public required YandexTrack Track { get; init; }

    [JsonPropertyName("originalIndex")]
    public int Index { get; init; }
}

/// <summary>
/// Special type of cover. May contains multiple images inside.
/// </summary>
public record YandexPlaylistCover : YandexCover
{
    [JsonPropertyName("itemsUri")]
    public  List<string>? ItemsUri { get; init; }
}

/// <summary>
/// Owner of playlist. Maps to Subsonic Curator Name.
/// </summary>
public record YandexPlaylistOwner
{
    [JsonPropertyName("login")]
    public required string Login { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}


/// <summary>
/// Yandex provides single "Best Result" in their Search API responses.
/// It can be on of: Track, Album, Artist
/// This class holds JsonElement for the result and tries to provide "Best Result"
/// with appropriate type based on "type" field value.
/// </summary>
public record YandexSearchBestResult
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("result")]
    public required JsonElement Result { get; init; }

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

public record YandexDownloadInfoWrapper
{
    [JsonPropertyName("name")]
    public string? ErrorName { get; init; }

    [JsonPropertyName("message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("downloadInfo")]
    public YandexDownloadInfo? DownloadInfo { get; init; }
}

public record YandexDownloadInfo
{
    [JsonPropertyName("bitrate")]
    public int Bitrate { get; init; }

    [JsonPropertyName("codec")]
    public required string Codec { get; init; }

    [JsonPropertyName("quality")]
    public required string Quality { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("urls")]
    public List<string> Urls { get; init; } = new();

    [JsonPropertyName("key")]
    public required string Key { get; init; }
}


/// <summary>
/// /tracks/{id}/download-info returns a list of download options.
/// This class represents a single option in that list.
/// This is used in legacy method of downloading. 
/// </summary>
public record YandexDownloadOptionLegacy
{
    [JsonPropertyName("downloadInfoUrl")]
    public required string Url { get; init; }

    [JsonPropertyName("bitrateInKbps")]
    public required int BitRate { get; init; }

    [JsonPropertyName("codec")]
    public required string Codec { get; init; }
}

/// <summary>
/// Details required to build song download URI.
/// Obtained from an URL provided by YandexTrackDownloadOptionLegacy
/// This is used in legacy method of downloading
/// </summary>
public record YandexDownloadInfoLegacy
{
    public required string Host { get; init; }
    public required string Path { get; init; }
    public required string Ts { get; init; }
    public required string S { get; init; }
}

public record YandexUserAccountStatus
{
    [JsonPropertyName("plus")]
    public YandexPlusStatus? PlusStatus { get; init; }
}

public record YandexPlusStatus
{
    [JsonPropertyName("hasPlus")]
    public bool HasPlus { get; init; }
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
