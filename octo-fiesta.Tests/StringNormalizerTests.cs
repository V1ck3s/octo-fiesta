using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

public class StringNormalizerTests
{
    [Fact]
    public void NormalizeForComparison_WithCurlyApostrophe_ReturnsNormalizedString()
    {
        // Arrange
        var input = "The Craving (Jenna‘s Version)"; // Curly apostrophe
        var expected = "The Craving (Jenna's Version)"; // Straight apostrophe

        // Act
        var result = StringNormalizer.NormalizeForComparison(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeForComparison_WithBacktick_ReturnsNormalizedString()
    {
        // Arrange
        var input = "The Craving (Jenna`s Version)";
        var expected = "The Craving (Jenna's Version)";

        // Act
        var result = StringNormalizer.NormalizeForComparison(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeForComparison_WithCurlyDoubleQuotes_ReturnsNormalizedString()
    {
        // Arrange
        var input = "“Hello World”"; // Curly double quotes
        var expected = "\"Hello World\""; // Straight double quotes

        // Act
        var result = StringNormalizer.NormalizeForComparison(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeForComparison_WithLeftSingleQuotationMark_ReturnsNormalizedString()
    {
        // Arrange
        var input = "‘Hello"; // Left single quotation mark
        var expected = "'Hello"; // Straight apostrophe

        // Act
        var result = StringNormalizer.NormalizeForComparison(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeForComparison_WithNoSpecialQuotes_ReturnsUnchanged()
    {
        // Arrange
        var input = "Normal Song Title";

        // Act
        var result = StringNormalizer.NormalizeForComparison(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeForComparison_WithEmptyString_ReturnsEmptyString()
    {
        // Arrange
        var input = "";

        // Act
        var result = StringNormalizer.NormalizeForComparison(input);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void NormalizeForComparison_WithNull_ReturnsEmptyString()
    {
        // Arrange
        string? input = null;

        // Act
        var result = StringNormalizer.NormalizeForComparison(input);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void CreateComparisonKey_WithMixedCase_ReturnsCaseInsensitiveKey()
    {
        // Arrange
        var input1 = "It'S A Song";
        var input2 = "it's a song";

        // Act
        var key1 = StringNormalizer.CreateComparisonKey(input1);
        var key2 = StringNormalizer.CreateComparisonKey(input2);

        // Assert
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void CreateComparisonKey_WithDifferentQuotes_ReturnsSameKey()
    {
        // Arrange
        var input1 = "It's"; // Straight apostrophe
        var input2 = "It’s"; // Curly apostrophe (U+2019)
        var input3 = "It`s"; // Backtick

        // Act
        var key1 = StringNormalizer.CreateComparisonKey(input1);
        var key2 = StringNormalizer.CreateComparisonKey(input2);
        var key3 = StringNormalizer.CreateComparisonKey(input3);

        // Assert
        Assert.Equal(key1, key2);
        Assert.Equal(key1, key3);
    }
}

