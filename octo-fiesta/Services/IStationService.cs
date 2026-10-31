using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services;

/// <summary>
/// A personal radio of the provider (Yandex Music "My Wave") offered to clients as a playlist.
/// Each read of the playlist continues the station; playback reports go back to the provider
/// so it can tune the next tracks.
/// </summary>
public interface IStationService
{
    /// <summary>
    /// The playlist entry clients see in getPlaylists; its ID is an external playlist ID
    /// (pl-{provider}-{id}) served by the metadata service.
    /// </summary>
    ExternalPlaylist StationPlaylist { get; }

    /// <summary>
    /// Reports that a song started playing. Songs that did not come from the station are ignored.
    /// Fire-and-forget: never throws and never delays playback.
    /// </summary>
    void TrackStarted(string provider, string externalId);

    /// <summary>
    /// Reports that a song was played to the end. Songs that did not come from the station are ignored.
    /// Fire-and-forget: never throws.
    /// </summary>
    void TrackFinished(string provider, string externalId);
}
