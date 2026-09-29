using Moq;
using Microsoft.Extensions.Logging;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Local;
using octo_fiesta.Services.Subsonic;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace octo_fiesta.Tests;

public class SubsonicModelMapperDeduplicationTests
{
    private readonly SubsonicModelMapper _mapper;
    private readonly Mock<ILogger<SubsonicModelMapper>> _mockLogger;

    public SubsonicModelMapperDeduplicationTests()
    {
        var responseBuilder = new SubsonicResponseBuilder();
        _mockLogger = new Mock<ILogger<SubsonicModelMapper>>();
        _mapper = new SubsonicModelMapper(responseBuilder, _mockLogger.Object);
    }

    [Fact]
    public void MergeSearchResults_Json_CaseSensitiveArtistDedup()
    {
        // Arrange
        var localArtists = new List<object>
        {
            new Dictionary<string, object> { ["id"] = "local1", ["name"] = "Test Artist" }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>
            {
                new Artist { Id = "ext1", Name = "test artist" }
            }
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), localArtists, externalResult, new List<ExternalPlaylist>(), null, true);

        Assert.Equal(2, mergedArtists.Count);
    }


    [Fact]
    public void MergeSearchResults_DropsExternalSong_WhenMappingHasLocalIdPresentInLocalSongs()
    {
        // Local search result contains the Navidrome song that the ext id maps to.
        var localSongs = new List<object>
        {
            new Dictionary<string, object>
            {
                ["id"] = "local-song-7",
                ["title"] = "Some Track",
                ["artist"] = "Some Artist"
            }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song
                {
                    Title = "Different Title",
                    Artist = "Different Artist",
                    ExternalProvider = "qobuz",
                    ExternalId = "123"
                }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var mappings = new Dictionary<string, LocalSongMapping>
        {
            ["qobuz:123"] = new LocalSongMapping
            {
                ExternalProvider = "qobuz",
                ExternalId = "123",
                LocalSubsonicId = "local-song-7",
                LocalPath = "/tmp/file.flac"
            }
        };

        var (mergedSongs, _, _) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(),
            externalResult, new List<ExternalPlaylist>(), mappings, true);

        Assert.Single(mergedSongs); // only the local song; ext dropped
    }

    [Fact]
    public void MergeSearchResults_DropsExternalSong_WhenArtistTitleMatchesLocal_NoMapping()
    {
        // Library predates Octo Fiesta, so .mappings.json has no entry for this song.
        var localSongs = new List<object>
        {
            new Dictionary<string, object>
            {
                ["id"] = "local-100",
                ["title"] = "Let It Happen",
                ["artist"] = "Tame Impala"
            }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song
                {
                    Title = "Let It Happen",
                    Artist = "Tame Impala",
                    ExternalProvider = "qobuz",
                    ExternalId = "999"
                }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var mappings = new Dictionary<string, LocalSongMapping>();

        var (mergedSongs, _, _) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(),
            externalResult, new List<ExternalPlaylist>(), mappings, true);

        Assert.Single(mergedSongs);
    }

    [Fact]
    public void MergeSearchResults_DropsExternalSong_WhenLocalTitleMatchesWithoutFeat()
    {
        var localSongs = new List<object>
        {
            new Dictionary<string, object>
            {
                ["id"] = "local-boi",
                ["title"] = "Boi",
                ["artist"] = "JPEGMAFIA"
            }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song
                {
                    Title = "Boi (feat. Butch Dawson)",
                    Artist = "JPEGMAFIA",
                    ExternalProvider = "qobuz",
                    ExternalId = "1"
                }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var (mergedSongs, _, _) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(),
            externalResult, new List<ExternalPlaylist>(), null, true);

        Assert.Single(mergedSongs);
    }

    [Fact]
    public void MergeSearchResults_KeepsExternalSong_WhenMappingExistsButLocalIdMissing()
    {
        // User deleted the song from Navidrome but the mapping row still exists.
        // The local search no longer returns the mapped id and metadata doesn't match,
        // so the ext result must remain visible.
        var localSongs = new List<object>(); // Navidrome no longer surfaces it
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song
                {
                    Title = "Orphan Track",
                    Artist = "Some Artist",
                    ExternalProvider = "qobuz",
                    ExternalId = "123"
                }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var mappings = new Dictionary<string, LocalSongMapping>
        {
            ["qobuz:123"] = new LocalSongMapping
            {
                ExternalProvider = "qobuz",
                ExternalId = "123",
                LocalSubsonicId = "local-song-7",
                LocalPath = "/tmp/file.flac"
            }
        };

        var (mergedSongs, _, _) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(),
            externalResult, new List<ExternalPlaylist>(), mappings, true);

        Assert.Single(mergedSongs);
    }

    [Fact]
    public void MergeSearchResults_DropsExternalAlbum_WhenArtistAlbumTitleMatchesLocal()
    {
        var localAlbums = new List<object>
        {
            new Dictionary<string, object>
            {
                ["id"] = "local-album-1",
                ["name"] = "Currents",
                ["artist"] = "Tame Impala"
            }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>
            {
                new Album
                {
                    Id = "ext-qobuz-album-555",
                    Title = "Currents",
                    Artist = "Tame Impala"
                }
            },
            Artists = new List<Artist>()
        };

        var (_, mergedAlbums, _) = _mapper.MergeSearchResults(
            new List<object>(), localAlbums, new List<object>(),
            externalResult, new List<ExternalPlaylist>(),
            new Dictionary<string, LocalSongMapping>(), true);

        Assert.Single(mergedAlbums);
    }

    [Fact]
    public void MergeSearchResults_KeepsAllExternal_WhenLocalListsAreEmpty()
    {
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song { Title = "A", Artist = "Artist", ExternalProvider = "qobuz", ExternalId = "1" },
                new Song { Title = "B", Artist = "Artist", ExternalProvider = "qobuz", ExternalId = "2" }
            },
            Albums = new List<Album>
            {
                new Album { Id = "ext-album-1", Title = "Album A", Artist = "Artist" }
            },
            Artists = new List<Artist>
            {
                new Artist { Id = "ext-artist-1", Name = "Artist" }
            }
        };

        var (mergedSongs, mergedAlbums, mergedArtists) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), new List<object>(),
            externalResult, new List<ExternalPlaylist>(),
            new Dictionary<string, LocalSongMapping>(), true);

        Assert.Equal(2, mergedSongs.Count);
        Assert.Single(mergedAlbums);
        Assert.Single(mergedArtists);
    }

    [Fact]
    public void MergeSearchResults_StillDedupesArtistsByName_WithMappings()
    {
        var localArtists = new List<object>
        {
            new Dictionary<string, object> { ["id"] = "local-1", ["name"] = "Tame Impala" }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>
            {
                new Artist { Id = "ext-1", Name = "tame impala" },
                new Artist { Id = "ext-2", Name = "Other Artist" }
            }
        };

        var (_, _, mergedArtists) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), localArtists,
            externalResult, new List<ExternalPlaylist>(),
            new Dictionary<string, LocalSongMapping>(), true);

        Assert.Equal(3, mergedArtists.Count);
    }

    [Fact]
    public void MergeSearchResults_DropsExternalSong_WhenArtistDiffersOnlyByCase()
    {
        var localSongs = new List<object>
        {
            new Dictionary<string, object>
            {
                ["id"] = "local-1",
                ["title"] = "Track One",
                ["artist"] = "leroy"
            }
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song
                {
                    Title = "Track One",
                    Artist = "Leroy",
                    ExternalProvider = "qobuz",
                    ExternalId = "1"
                }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };

        var (mergedSongs, _, _) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(),
            externalResult, new List<ExternalPlaylist>(), null, true);

        Assert.Single(mergedSongs);
    }

    [Fact]
    public void MergeSearchResults_Xml_DropsExternalSong_WhenMappingMatches()
    {
        var localSongs = new List<object>
        {
            new XElement("song",
                new XAttribute("id", "local-song-7"),
                new XAttribute("title", "Some Track"),
                new XAttribute("artist", "Some Artist"))
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>
            {
                new Song
                {
                    Title = "Different Title",
                    Artist = "Different Artist",
                    ExternalProvider = "qobuz",
                    ExternalId = "123"
                }
            },
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };
        var mappings = new Dictionary<string, LocalSongMapping>
        {
            ["qobuz:123"] = new LocalSongMapping
            {
                ExternalProvider = "qobuz",
                ExternalId = "123",
                LocalSubsonicId = "local-song-7",
                LocalPath = "/tmp/file.flac"
            }
        };

        var (mergedSongs, _, _) = _mapper.MergeSearchResults(
            localSongs, new List<object>(), new List<object>(),
            externalResult, new List<ExternalPlaylist>(), mappings, false);

        Assert.Single(mergedSongs);
    }

    [Fact]
    public void MergeSearchResults_Xml_DropsExternalAlbum_WhenArtistAlbumTitleMatchesLocal()
    {
        var localAlbums = new List<object>
        {
            new XElement("album",
                new XAttribute("id", "local-album-1"),
                new XAttribute("name", "Currents"),
                new XAttribute("artist", "Tame Impala"))
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>
            {
                new Album
                {
                    Id = "ext-qobuz-album-555",
                    Title = "Currents",
                    Artist = "Tame Impala"
                }
            },
            Artists = new List<Artist>()
        };

        var (_, mergedAlbums, _) = _mapper.MergeSearchResults(
            new List<object>(), localAlbums, new List<object>(),
            externalResult, new List<ExternalPlaylist>(),
            new Dictionary<string, LocalSongMapping>(), false);

        Assert.Single(mergedAlbums);
    }

    [Fact]
    public void MergeSearchResults_DedupesExternalPlaylists_BySameProviderNameAndCurator()
    {
        // Qobuz returns several yearly snapshots of the same curated list with the
        // same name and curator but distinct ids; we want exactly one playlist-album.
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };
        var playlists = new List<ExternalPlaylist>
        {
            new ExternalPlaylist
            {
                Id = "pl-qobuz-1",
                Name = "Electroclash Essentials",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-2",
                Name = "Electroclash Essentials",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-3",
                Name = "Electroclash Essentials",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            }
        };

        var (_, mergedAlbums, _) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), new List<object>(),
            externalResult, playlists, null, true);

        Assert.Single(mergedAlbums);
    }

    [Fact]
    public void MergeSearchResults_KeepsExternalPlaylists_WhenCuratorDiffers()
    {
        // Two distinct curators publishing playlists that happen to share a generic title
        // are NOT duplicates - keep both.
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };
        var playlists = new List<ExternalPlaylist>
        {
            new ExternalPlaylist
            {
                Id = "pl-qobuz-1",
                Name = "Workout",
                CuratorName = "DJ A",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-2",
                Name = "Workout",
                CuratorName = "DJ B",
                Provider = "qobuz"
            }
        };

        var (_, mergedAlbums, _) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), new List<object>(),
            externalResult, playlists, null, true);

        Assert.Equal(2, mergedAlbums.Count);
    }

    [Fact]
    public void MergeSearchResults_DedupesExternalPlaylists_CaseInsensitive()
    {
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };
        var playlists = new List<ExternalPlaylist>
        {
            new ExternalPlaylist
            {
                Id = "pl-qobuz-1",
                Name = "Electroclash Essentials",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-2",
                Name = "  electroclash   ESSENTIALS  ",
                CuratorName = "qobuz steve",
                Provider = "QOBUZ"
            }
        };

        var (_, mergedAlbums, _) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), new List<object>(),
            externalResult, playlists, null, true);

        Assert.Single(mergedAlbums);
    }

    [Fact]
    public void MergeSearchResults_Xml_DedupesExternalPlaylists_BySameProviderNameAndCurator()
    {
        var localAlbums = new List<object>
        {
            new XElement("album",
                new XAttribute("id", "local-album-1"),
                new XAttribute("name", "Some Local Album"),
                new XAttribute("artist", "Some Artist"))
        };
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };
        var playlists = new List<ExternalPlaylist>
        {
            new ExternalPlaylist
            {
                Id = "pl-qobuz-1",
                Name = "Electroclash Essentials",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-2",
                Name = "Electroclash Essentials",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-3",
                Name = "Electroclash Essentials",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            }
        };

        var (_, mergedAlbums, _) = _mapper.MergeSearchResults(
            new List<object>(), localAlbums, new List<object>(),
            externalResult, playlists, null, false);

        // 1 local album + 1 deduped playlist-as-album = 2
        Assert.Equal(2, mergedAlbums.Count);
    }

    [Fact]
    public void MergeSearchResults_KeepsExternalPlaylists_WhenNameMissing()
    {
        // Defensive: never collapse rows we cannot identify.
        var externalResult = new SearchResult
        {
            Songs = new List<Song>(),
            Albums = new List<Album>(),
            Artists = new List<Artist>()
        };
        var playlists = new List<ExternalPlaylist>
        {
            new ExternalPlaylist
            {
                Id = "pl-qobuz-1",
                Name = "",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-2",
                Name = "   ",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            },
            new ExternalPlaylist
            {
                Id = "pl-qobuz-3",
                Name = "",
                CuratorName = "Qobuz Steve",
                Provider = "qobuz"
            }
        };

        var (_, mergedAlbums, _) = _mapper.MergeSearchResults(
            new List<object>(), new List<object>(), new List<object>(),
            externalResult, playlists, null, true);

        Assert.Equal(3, mergedAlbums.Count);
    }
}
