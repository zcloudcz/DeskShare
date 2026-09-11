using DeskShare.Core.Models;
using Xunit;

namespace DeskShare.UnitTests.Clipboard;

/// <summary>
/// Unit tests for ClipboardMessage model.
/// Tests serialization, deserialization, and basic functionality.
/// </summary>
public class ClipboardMessageTests
{
    [Fact]
    public void Constructor_ShouldSetDefaultValues()
    {
        // Arrange & Act
        var message = new ClipboardMessage();

        // Assert
        Assert.Equal(ClipboardContentType.Text, message.ContentType); // Default enum value
        Assert.Null(message.TextContent);
        Assert.Null(message.ImageDataBase64);
        Assert.Null(message.FilePaths);
        Assert.Equal(0, message.SizeBytes);
        Assert.Equal(0, message.SequenceNumber);
        Assert.True(DateTime.UtcNow - message.Timestamp < TimeSpan.FromSeconds(1)); // Should be recent
    }

    [Fact]
    public void TextMessage_ShouldCalculateCorrectSize()
    {
        // Arrange
        var text = "Hello, World!";
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(text);

        // Act
        var message = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Text,
            TextContent = text,
            SizeBytes = expectedSize
        };

        // Assert
        Assert.Equal(ClipboardContentType.Text, message.ContentType);
        Assert.Equal(text, message.TextContent);
        Assert.Equal(expectedSize, message.SizeBytes);
        Assert.True(message.SizeBytes > 0);
    }

    [Fact]
    public void ImageMessage_ShouldHandleBase64Data()
    {
        // Arrange
        var imageData = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });

        // Act
        var message = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Image,
            ImageDataBase64 = imageData,
            SizeBytes = imageData.Length
        };

        // Assert
        Assert.Equal(ClipboardContentType.Image, message.ContentType);
        Assert.Equal(imageData, message.ImageDataBase64);
        Assert.Null(message.TextContent); // Should be null for image
        Assert.True(message.SizeBytes > 0);
    }

    [Fact]
    public void SequenceNumber_ShouldIncrementCorrectly()
    {
        // Arrange & Act
        var message1 = new ClipboardMessage { SequenceNumber = 1 };
        var message2 = new ClipboardMessage { SequenceNumber = 2 };
        var message3 = new ClipboardMessage { SequenceNumber = 3 };

        // Assert
        Assert.Equal(1, message1.SequenceNumber);
        Assert.Equal(2, message2.SequenceNumber);
        Assert.Equal(3, message3.SequenceNumber);
        Assert.True(message3.SequenceNumber > message2.SequenceNumber);
        Assert.True(message2.SequenceNumber > message1.SequenceNumber);
    }

    [Theory]
    [InlineData(ClipboardContentType.Text)]
    [InlineData(ClipboardContentType.Image)]
    [InlineData(ClipboardContentType.Html)]
    [InlineData(ClipboardContentType.Rtf)]
    [InlineData(ClipboardContentType.Files)]
    public void ContentType_ShouldSupportAllTypes(ClipboardContentType contentType)
    {
        // Arrange & Act
        var message = new ClipboardMessage
        {
            ContentType = contentType
        };

        // Assert
        Assert.Equal(contentType, message.ContentType);
    }

    [Fact]
    public void FilePaths_ShouldStoreMultiplePaths()
    {
        // Arrange
        var paths = new List<string>
        {
            "C:\\Documents\\file1.pdf",
            "C:\\Documents\\file2.doc",
            "C:\\Pictures\\image.png"
        };

        // Act
        var message = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Files,
            FilePaths = paths
        };

        // Assert
        Assert.Equal(ClipboardContentType.Files, message.ContentType);
        Assert.Equal(3, message.FilePaths!.Count);
        Assert.Contains("C:\\Documents\\file1.pdf", message.FilePaths);
        Assert.Contains("C:\\Pictures\\image.png", message.FilePaths);
    }

    [Fact]
    public void Timestamp_ShouldBeUtc()
    {
        // Arrange & Act
        var message = new ClipboardMessage();

        // Assert
        Assert.Equal(DateTimeKind.Utc, message.Timestamp.Kind);
    }

    [Fact]
    public void LargeTextMessage_ShouldCalculateCorrectSize()
    {
        // Arrange - Create a large text (10,000 characters)
        var largeText = new string('A', 10000);
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(largeText);

        // Act
        var message = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Text,
            TextContent = largeText,
            SizeBytes = expectedSize
        };

        // Assert
        Assert.Equal(10000, message.SizeBytes); // ASCII 'A' is 1 byte in UTF-8
        Assert.Equal(largeText.Length, message.TextContent!.Length);
    }

    [Fact]
    public void UnicodeTextMessage_ShouldCalculateCorrectSize()
    {
        // Arrange - Unicode characters take multiple bytes in UTF-8
        var unicodeText = "Hello 世界 🌍"; // Mix of ASCII, Chinese, and emoji
        var expectedSize = System.Text.Encoding.UTF8.GetByteCount(unicodeText);

        // Act
        var message = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Text,
            TextContent = unicodeText,
            SizeBytes = expectedSize
        };

        // Assert
        Assert.Equal(unicodeText, message.TextContent);
        Assert.True(message.SizeBytes > unicodeText.Length); // UTF-8 bytes > character count
        Assert.Equal(expectedSize, message.SizeBytes);
    }

    [Fact]
    public void EmptyTextMessage_ShouldHaveZeroSize()
    {
        // Arrange & Act
        var message = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Text,
            TextContent = string.Empty,
            SizeBytes = 0
        };

        // Assert
        Assert.Equal(string.Empty, message.TextContent);
        Assert.Equal(0, message.SizeBytes);
    }

    [Fact]
    public void NullTextContent_ShouldBeValid()
    {
        // Arrange & Act
        var message = new ClipboardMessage
        {
            ContentType = ClipboardContentType.Image,
            TextContent = null, // Image messages don't have text
            ImageDataBase64 = "base64data"
        };

        // Assert
        Assert.Null(message.TextContent);
        Assert.NotNull(message.ImageDataBase64);
    }
}
