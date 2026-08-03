using octo_fiesta.Services.Common;
using Xunit;

namespace octo_fiesta.Tests;

public class PlaylistIdHelperTests
{
    #region IsExternalPlaylist Tests

    [Fact]
    public void IsExternalPlaylist_WithValidPlaylistId_ReturnsTrue()
    {
        // Arrange
        var id = "pl-yandex-123456";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithValidQobuzPlaylistId_ReturnsTrue()
    {
        // Arrange
        var id = "pl-qobuz-789012";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithUpperCasePrefix_ReturnsTrue()
    {
        // Arrange
        var id = "PL-yandex-123456";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithRegularAlbumId_ReturnsFalse()
    {
        // Arrange
        var id = "ext-qobuz-album-123456";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithNullId_ReturnsFalse()
    {
        // Arrange
        string? id = null;

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithEmptyString_ReturnsFalse()
    {
        // Arrange
        var id = "";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithRandomString_ReturnsFalse()
    {
        // Arrange
        var id = "random-string-123";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithNavidromePlaylistId_ReturnsFalse()
    {
        // Arrange - Navidrome playlist IDs start with "pl-" but have a different format
        var id = "pl-vpF9RQkDvQJicUQFTYlOJS_697738a2";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsExternalPlaylist_WithYouTubePlaylistId_ReturnsTrue()
    {
        var id = "pl-youtube-12345678";

        // Act
        var result = PlaylistIdHelper.IsExternalPlaylist(id);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region ParsePlaylistId Tests

    [Fact]
    public void ParsePlaylistId_WithValidYandexPlaylistId_ReturnsProviderAndExternalId()
    {
        // Arrange
        var id = "pl-yandex-123456";

        // Act
        var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

        // Assert
        Assert.Equal("yandex", provider);
        Assert.Equal("123456", externalId);
    }

    [Fact]
    public void ParsePlaylistId_WithValidQobuzPlaylistId_ReturnsProviderAndExternalId()
    {
        // Arrange
        var id = "pl-qobuz-789012";

        // Act
        var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

        // Assert
        Assert.Equal("qobuz", provider);
        Assert.Equal("789012", externalId);
    }

    [Fact]
    public void ParsePlaylistId_WithExternalIdContainingDashes_ParsesCorrectly()
    {
        // Arrange
        var id = "pl-yandex-abc-def-123";

        // Act
        var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

        // Assert
        Assert.Equal("yandex", provider);
        Assert.Equal("abc-def-123", externalId);
    }

    [Fact]
    public void ParsePlaylistId_WithInvalidFormatNoProvider_ThrowsArgumentException()
    {
        // Arrange
        var id = "pl-123456";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
        Assert.Contains("Invalid playlist ID format", exception.Message);
    }

    [Fact]
    public void ParsePlaylistId_WithNonPlaylistId_ThrowsArgumentException()
    {
        // Arrange
        var id = "ext-qobuz-album-123456";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
        Assert.Contains("Invalid playlist ID format", exception.Message);
    }

    [Fact]
    public void ParsePlaylistId_WithNullId_ThrowsArgumentException()
    {
        // Arrange
        string? id = null;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id!));
    }

    [Fact]
    public void ParsePlaylistId_WithEmptyString_ThrowsArgumentException()
    {
        // Arrange
        var id = "";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
    }

    [Fact]
    public void ParsePlaylistId_WithOnlyPrefix_ThrowsArgumentException()
    {
        // Arrange
        var id = "pl-";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.ParsePlaylistId(id));
        Assert.Contains("Invalid playlist ID format", exception.Message);
    }

    #endregion

    #region CreatePlaylistId Tests

    [Fact]
    public void CreatePlaylistId_WithValidYandexProviderAndId_ReturnsCorrectFormat()
    {
        // Arrange
        var provider = "yandex";
        var externalId = "123456";

        // Act
        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        // Assert
        Assert.Equal("pl-yandex-123456", result);
    }

    [Fact]
    public void CreatePlaylistId_WithValidQobuzProviderAndId_ReturnsCorrectFormat()
    {
        // Arrange
        var provider = "qobuz";
        var externalId = "789012";

        // Act
        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        // Assert
        Assert.Equal("pl-qobuz-789012", result);
    }

    [Fact]
    public void CreatePlaylistId_WithUpperCaseProvider_ConvertsToLowerCase()
    {
        // Arrange
        var provider = "YANDEX";
        var externalId = "123456";

        // Act
        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        // Assert
        Assert.Equal("pl-yandex-123456", result);
    }

    [Fact]
    public void CreatePlaylistId_WithMixedCaseProvider_ConvertsToLowerCase()
    {
        // Arrange
        var provider = "YaNdEx";
        var externalId = "123456";

        // Act
        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        // Assert
        Assert.Equal("pl-yandex-123456", result);
    }

    [Fact]
    public void CreatePlaylistId_WithExternalIdContainingDashes_PreservesDashes()
    {
        // Arrange
        var provider = "yandex";
        var externalId = "abc-def-123";

        // Act
        var result = PlaylistIdHelper.CreatePlaylistId(provider, externalId);

        // Assert
        Assert.Equal("pl-yandex-abc-def-123", result);
    }

    [Fact]
    public void CreatePlaylistId_WithNullProvider_ThrowsArgumentException()
    {
        // Arrange
        string? provider = null;
        var externalId = "123456";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider!, externalId));
        Assert.Contains("Provider cannot be null or empty", exception.Message);
    }

    [Fact]
    public void CreatePlaylistId_WithEmptyProvider_ThrowsArgumentException()
    {
        // Arrange
        var provider = "";
        var externalId = "123456";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider, externalId));
        Assert.Contains("Provider cannot be null or empty", exception.Message);
    }

    [Fact]
    public void CreatePlaylistId_WithNullExternalId_ThrowsArgumentException()
    {
        // Arrange
        var provider = "yandex";
        string? externalId = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider, externalId!));
        Assert.Contains("External ID cannot be null or empty", exception.Message);
    }

    [Fact]
    public void CreatePlaylistId_WithEmptyExternalId_ThrowsArgumentException()
    {
        // Arrange
        var provider = "yandex";
        var externalId = "";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => PlaylistIdHelper.CreatePlaylistId(provider, externalId));
        Assert.Contains("External ID cannot be null or empty", exception.Message);
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_CreateAndParse_ReturnsOriginalValues()
    {
        // Arrange
        var originalProvider = "yandex";
        var originalExternalId = "123456";

        // Act
        var playlistId = PlaylistIdHelper.CreatePlaylistId(originalProvider, originalExternalId);
        var (parsedProvider, parsedExternalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

        // Assert
        Assert.Equal(originalProvider, parsedProvider);
        Assert.Equal(originalExternalId, parsedExternalId);
    }

    [Fact]
    public void RoundTrip_CreateWithUpperCaseAndParse_ReturnsLowerCaseProvider()
    {
        // Arrange
        var originalProvider = "QOBUZ";
        var originalExternalId = "789012";

        // Act
        var playlistId = PlaylistIdHelper.CreatePlaylistId(originalProvider, originalExternalId);
        var (parsedProvider, parsedExternalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

        // Assert
        Assert.Equal("qobuz", parsedProvider); // Converted to lowercase
        Assert.Equal(originalExternalId, parsedExternalId);
    }

    [Fact]
    public void RoundTrip_WithComplexExternalId_PreservesValue()
    {
        // Arrange
        var originalProvider = "yandex";
        var originalExternalId = "abc-123-def-456";

        // Act
        var playlistId = PlaylistIdHelper.CreatePlaylistId(originalProvider, originalExternalId);
        var (parsedProvider, parsedExternalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

        // Assert
        Assert.Equal(originalProvider, parsedProvider);
        Assert.Equal(originalExternalId, parsedExternalId);
    }

    #endregion
}
