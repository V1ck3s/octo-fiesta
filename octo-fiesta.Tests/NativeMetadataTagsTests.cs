using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

public class NativeMetadataTagsTests
{
    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "2")]
    [InlineData(null, null)]
    public async Task FlacTagsSurviveReadback(bool? advisory, string? expected)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".flac");
        // A 10 ms silent FLAC generated with ffmpeg; no external fixture or media download.
        File.WriteAllBytes(path, Convert.FromBase64String("ZkxhQwAAACIBuQG5AAANAAANCsRA8AAAAbnl+CiEcM+L0UCXUFCB5jQfhAAADgYAAABmZm1wZWcAAAAA//h5CAABuFUAAACMQw=="));
        try
        {
            await new MetadataWriter().Write(path, new Song { Title = "Song", Artist = "Artist", Album = "Album",
                Isrc = "GBTEST000001", Barcode = "0012345678901", Label = "Label", IsExplicit = advisory,
                Copyright = "Copyright Owner" });
            using var reread = TagLib.File.Create(path);
            var tags = Assert.IsType<TagLib.Ogg.XiphComment>(reread.GetTag(TagLib.TagTypes.Xiph));
            Assert.Equal("GBTEST000001", tags.GetFirstField("ISRC"));
            Assert.Equal("0012345678901", tags.GetFirstField("BARCODE"));
            Assert.Equal("Label", tags.GetFirstField("LABEL"));
            Assert.Equal(expected, tags.GetFirstField("ITUNESADVISORY"));
            Assert.Equal("Copyright Owner", reread.Tag.Copyright);
        }
        finally { File.Delete(path); }
    }

    private sealed class MetadataWriter : BaseDownloadService
    {
        internal MetadataWriter() : base(Moq.Mock.Of<IHttpClientFactory>(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Moq.Mock.Of<octo_fiesta.Services.Local.ILocalLibraryService>(),
            Moq.Mock.Of<octo_fiesta.Services.IMusicMetadataService>(), new octo_fiesta.Models.Settings.SubsonicSettings(),
            Moq.Mock.Of<IServiceProvider>(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { }
        protected override string ProviderName => "qobuz";
        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);
        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;
        protected override string? GetTargetQuality() => null;
        protected override Task<DownloadResult> DownloadTrackAsync(string id, Song song,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        internal Task Write(string path, Song song) => WriteMetadataAsync(path, song, CancellationToken.None);
    }
}
