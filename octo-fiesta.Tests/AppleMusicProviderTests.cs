using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services;
using octo_fiesta.Services.AppleMusic;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Tests;

public class AppleMusicProviderTests : IDisposable
{
    private readonly string _library;

    public AppleMusicProviderTests()
    {
        _library = Path.Combine(Path.GetTempPath(), "octo-fiesta-applemusic-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_library);
    }

    public void Dispose()
    {
        if (Directory.Exists(_library)) Directory.Delete(_library, true);
    }

    private static AlacarteSong Song(string id, string title, string? albumId = "10", string? artistId = "20") =>
        new(id, title, "Artist", artistId, "Album", albumId, "Album Artist", 3, 1, 321_600, "USRC10000001",
            2020, "2020-01-01", "Pop", null, true, "https://a/600.jpg", "https://a/1400.jpg");

    [Fact]
    public void Mapper_BuildsExternalIdsAndMetadata()
    {
        var song = AppleMusicMapper.ToSong(Song("1", "Title"));
        Assert.Equal("applemusic", song.ExternalProvider);
        Assert.Equal("1", song.ExternalId);
        Assert.Equal("ext-applemusic-album-10", song.AlbumId);
        Assert.Equal("ext-applemusic-artist-20", song.ArtistId);
        Assert.Equal(322, song.Duration);
        Assert.Equal("Album Artist", song.AlbumArtist);
        Assert.Equal(1, song.ExplicitContentLyrics);
        Assert.False(song.IsLocal);

        var album = AppleMusicMapper.ToAlbum(new AlacarteAlbum("10", "Album", "Album Artist", "20", 2020, 2, "Pop",
            "album", "123", "Label", "(c)", "https://a/600.jpg", null, new() { Song("1", "A"), Song("2", "B") }));
        Assert.Equal("ext-applemusic-album-10", album.Id);
        Assert.Equal(new[] { "1", "2" }, album.Songs.Select(s => s.ExternalId));
        Assert.All(album.Songs, s => Assert.Equal(2, s.TotalTracks));
    }

    [Fact]
    public async Task Search_LeavesOutSongsAndAlbumsAlreadyInTheLibrary()
    {
        var json = "{\"songs\":[{\"id\":\"1\",\"title\":\"Owned\",\"artist\":\"A\",\"album\":\"X\",\"explicit\":false,\"inLibrary\":true},"
            + "{\"id\":\"2\",\"title\":\"New\",\"artist\":\"A\",\"album\":\"X\",\"explicit\":false,\"inLibrary\":false}],"
            + "\"albums\":[{\"id\":\"10\",\"title\":\"Owned Album\",\"artist\":\"A\",\"inLibrary\":true},"
            + "{\"id\":\"11\",\"title\":\"New Album\",\"artist\":\"A\",\"inLibrary\":false}],\"artists\":[],\"playlists\":[]}";
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("http://alacarte/api/integration/v1/") });
        var client = new AlacarteClient(factory.Object,
            Options.Create(new AppleMusicSettings { AlacarteUrl = "http://alacarte", ApiToken = "t" }), NullLogger<AlacarteClient>.Instance);

        var result = await new AppleMusicMetadataService(client).SearchAllAsync("x");

        Assert.Equal(new[] { "2" }, result.Songs.Select(s => s.ExternalId));
        Assert.Equal(new[] { "11" }, result.Albums.Select(a => a.ExternalId));
    }

    [Fact]
    public void PlaylistSongs_UseThePlaylistAsAlbum_AndIdsRoundTrip()
    {
        var playlist = new AlacartePlaylist("pl.abc-def", "Mix", "Apple Music", null, 2, null, null,
            new() { Song("1", "A"), Song("2", "B") });
        var songs = AppleMusicMapper.ToPlaylistSongs(playlist);
        Assert.Equal(new int?[] { 1, 2 }, songs.Select(s => s.Track));
        Assert.All(songs, s => Assert.Equal("Mix", s.Album));

        var id = AppleMusicMapper.ToPlaylist(playlist).Id;
        Assert.True(PlaylistIdHelper.IsExternalPlaylist(id));
        Assert.Equal(("applemusic", "pl.abc-def"), PlaylistIdHelper.ParsePlaylistId(id));
    }

    [Fact]
    public void ToLibraryPath_StaysInsideTheLibrary()
    {
        Assert.Equal(Path.Combine(_library, "A", "B", "01. C.flac"),
            AppleMusicDownloadService.ToLibraryPath(_library, "A/B/01. C.flac"));
        Assert.Throws<InvalidOperationException>(() => AppleMusicDownloadService.ToLibraryPath(_library, "../etc/passwd"));
        Assert.Throws<InvalidOperationException>(() => AppleMusicDownloadService.ToLibraryPath(_library, "/etc/passwd"));
    }

    [Fact]
    public async Task LibraryPathResult_IsUsedInPlace_WithoutCopyingOrRetagging()
    {
        // ALACarte's own layout, deliberately different from the folder template
        var alacarteFile = Path.Combine(_library, "Album Artist", "Album (2020)", "07. Title.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(alacarteFile)!);
        await File.WriteAllTextAsync(alacarteFile, "alacarte-file");

        var localLib = new Mock<ILocalLibraryService>();
        var meta = new Mock<IMusicMetadataService>();
        meta.Setup(x => x.GetSongAsync("fake", "1")).ReturnsAsync(new Song
        {
            ExternalId = "1", ExternalProvider = "fake", Title = "Title", Artist = "Artist", Album = "Album", Track = 7,
        });
        var service = new LibraryPathDownloadService(
            new Mock<IHttpClientFactory>().Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _library }).Build(),
            localLib.Object, meta.Object,
            new SubsonicSettings { FolderTemplate = "{artist}/{album}/{track}. {title}", StorageMode = StorageMode.Permanent, DownloadMode = DownloadMode.Track },
            new Mock<IServiceProvider>().Object, NullLogger.Instance) { LibraryFile = alacarteFile };

        var path = await service.DownloadSongAsync("fake", "1");

        Assert.Equal(alacarteFile, path);
        Assert.Equal("alacarte-file", await File.ReadAllTextAsync(alacarteFile));
        Assert.Single(Directory.GetFiles(_library, "*", SearchOption.AllDirectories));
        localLib.Verify(x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), alacarteFile, "FLAC", null), Times.Once);
    }

    [Fact]
    public void ProviderDisplayName_IsAppleMusic()
    {
        Assert.Equal("Apple Music", ProviderNames.Display("applemusic"));
        Assert.Equal("Qobuz", ProviderNames.Display("qobuz"));
    }

    [Fact]
    public async Task MultiProvider_SearchesBothAndRoutesByProvider()
    {
        var primary = new Mock<IMusicMetadataService>();
        primary.Setup(x => x.SearchAllAsync("q", 20, 20, 20)).ReturnsAsync(new SearchResult
        {
            Songs = new() { new Song { Title = "P", ExternalProvider = "qobuz", ExternalId = "1" } },
        });
        primary.Setup(x => x.GetSongAsync("qobuz", "1")).ReturnsAsync(new Song { Title = "from qobuz" });
        var apple = new Mock<IMusicMetadataService>();
        apple.Setup(x => x.SearchAllAsync("q", 20, 20, 20)).ReturnsAsync(new SearchResult
        {
            Songs = new() { new Song { Title = "A", ExternalProvider = "applemusic", ExternalId = "2" } },
        });
        apple.Setup(x => x.GetSongAsync("applemusic", "2")).ReturnsAsync(new Song { Title = "from apple" });
        var multi = new MultiProviderMetadataService(primary.Object, apple.Object);

        var result = await multi.SearchAllAsync("q");

        Assert.Equal(new[] { "P", "A" }, result.Songs.Select(s => s.Title));
        Assert.Equal("from qobuz", (await multi.GetSongAsync("qobuz", "1"))!.Title);
        Assert.Equal("from apple", (await multi.GetSongAsync("applemusic", "2"))!.Title);

        var primaryDl = new Mock<IDownloadService>();
        var appleDl = new Mock<IDownloadService>();
        var downloads = new MultiProviderDownloadService(primaryDl.Object, appleDl.Object);
        downloads.DownloadFullAlbumInBackground("applemusic", "9");
        downloads.DownloadFullAlbumInBackground("qobuz", "8");
        appleDl.Verify(x => x.DownloadFullAlbumInBackground("applemusic", "9"), Times.Once);
        primaryDl.Verify(x => x.DownloadFullAlbumInBackground("qobuz", "8"), Times.Once);
    }

    [Fact]
    public void Alongside_KeepsThePrimaryDiscoverableAndInjectsTheMultiProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<AppleMusicSettings>(s => { s.AlacarteUrl = "http://alacarte"; s.ApiToken = "t"; });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Library:DownloadPath"] = _library }).Build());
        services.AddSingleton(Options.Create(new SubsonicSettings()));
        services.AddSingleton(new Mock<ILocalLibraryService>().Object);
        services.AddSingleton<IMusicMetadataService, PrimaryMetadataService>();
        services.AddSingleton<IDownloadService, PrimaryDownloadService>();

        AppleMusicRegistration.AddAppleMusicAlongside(services, enableExternalPlaylists: false);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<MultiProviderMetadataService>(provider.GetRequiredService<IMusicMetadataService>());
        Assert.IsType<MultiProviderDownloadService>(provider.GetRequiredService<IDownloadService>());
        var all = provider.GetServices<IMusicMetadataService>().Select(s => s.GetType()).ToList();
        Assert.Contains(typeof(PrimaryMetadataService), all);
        Assert.Contains(typeof(AppleMusicMetadataService), all);
        var downloads = provider.GetServices<IDownloadService>().Select(s => s.GetType()).ToList();
        Assert.Contains(typeof(PrimaryDownloadService), downloads);
        Assert.Contains(typeof(AppleMusicDownloadService), downloads);
    }

    public sealed class PrimaryMetadataService : IMusicMetadataService
    {
        public Task<List<Song>> SearchSongsAsync(string query, int limit = 20) => Task.FromResult(new List<Song>());
        public Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20) => Task.FromResult(new List<Album>());
        public Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20) => Task.FromResult(new List<Artist>());
        public Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20) => Task.FromResult(new SearchResult());
        public Task<Song?> GetSongAsync(string externalProvider, string externalId) => Task.FromResult<Song?>(null);
        public Task<Album?> GetAlbumAsync(string externalProvider, string externalId) => Task.FromResult<Album?>(null);
        public Task<Artist?> GetArtistAsync(string externalProvider, string externalId) => Task.FromResult<Artist?>(null);
        public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId) => Task.FromResult(new List<Album>());
        public Task<List<octo_fiesta.Models.Subsonic.ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<octo_fiesta.Models.Subsonic.ExternalPlaylist>());
        public Task<octo_fiesta.Models.Subsonic.ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) => Task.FromResult<octo_fiesta.Models.Subsonic.ExternalPlaylist?>(null);
        public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) => Task.FromResult(new List<Song>());
    }

    public sealed class PrimaryDownloadService : BaseDownloadService
    {
        public PrimaryDownloadService(IHttpClientFactory f, IConfiguration c, ILocalLibraryService l, IMusicMetadataService m, IOptions<SubsonicSettings> s, IServiceProvider sp, ILogger<PrimaryDownloadService> log)
            : base(f, c, l, m, s.Value, sp, log) { }
        protected override string ProviderName => "primary";
        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);
        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;
        protected override string? GetTargetQuality() => null;
        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class LibraryPathDownloadService : BaseDownloadService
    {
        public string LibraryFile { get; set; } = "";

        public LibraryPathDownloadService(IHttpClientFactory f, IConfiguration c, ILocalLibraryService l, IMusicMetadataService m,
            SubsonicSettings s, IServiceProvider sp, Microsoft.Extensions.Logging.ILogger log) : base(f, c, l, m, s, sp, log) { }

        protected override string ProviderName => "fake";
        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);
        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;
        protected override string? GetTargetQuality() => null;

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadResult(Stream.Null, ".flac", "FLAC", LibraryPath: LibraryFile));
    }
}
