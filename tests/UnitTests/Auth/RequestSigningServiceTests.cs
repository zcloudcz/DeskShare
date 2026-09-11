using DeskShare.Core.Auth;
using Xunit;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Tests for HMAC request signing and validation.
/// Ensures cryptographic security of authentication requests.
/// </summary>
public class RequestSigningServiceTests
{
    [Fact]
    public void SignRequest_WithValidInputs_GeneratesSignature()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        // Act
        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Assert
        Assert.NotNull(signature);
        Assert.NotEmpty(signature);
        Assert.True(IsBase64String(signature), "Signature should be valid Base64");
    }

    [Fact]
    public void SignRequest_WithSameInputs_GeneratesSameSignature()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var nonce = "test-nonce-123";

        // Act
        var signature1 = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);
        var signature2 = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Assert
        Assert.Equal(signature1, signature2);
    }

    [Fact]
    public void SignRequest_WithDifferentServerId_GeneratesDifferentSignature()
    {
        // Arrange
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        // Act
        var signature1 = RequestSigningService.SignRequest("SERVER1", passkey, timestamp, nonce);
        var signature2 = RequestSigningService.SignRequest("SERVER2", passkey, timestamp, nonce);

        // Assert
        Assert.NotEqual(signature1, signature2);
    }

    [Fact]
    public void SignRequest_WithDifferentPasskey_GeneratesDifferentSignature()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        // Act
        var signature1 = RequestSigningService.SignRequest(serverId, "PASSKEY1", timestamp, nonce);
        var signature2 = RequestSigningService.SignRequest(serverId, "PASSKEY2", timestamp, nonce);

        // Assert
        Assert.NotEqual(signature1, signature2);
    }

    [Fact]
    public void SignRequest_WithDifferentNonce_GeneratesDifferentSignature()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;

        // Act
        var signature1 = RequestSigningService.SignRequest(serverId, passkey, timestamp, "nonce1");
        var signature2 = RequestSigningService.SignRequest(serverId, passkey, timestamp, "nonce2");

        // Assert
        Assert.NotEqual(signature1, signature2);
    }

    [Theory]
    [InlineData(null, "passkey", "nonce")]
    [InlineData("", "passkey", "nonce")]
    [InlineData("server", null, "nonce")]
    [InlineData("server", "", "nonce")]
    [InlineData("server", "passkey", null)]
    [InlineData("server", "passkey", "")]
    public void SignRequest_WithNullOrEmptyInputs_ThrowsArgumentNullException(
        string serverId, string passkey, string nonce)
    {
        // Arrange
        var timestamp = DateTime.UtcNow;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce));
    }

    [Fact]
    public void ValidateSignature_WithValidSignature_ReturnsTrue()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Act
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, timestamp, nonce, signature);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateSignature_WithTamperedServerId_ReturnsFalse()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Act - try to validate with different serverId
        var isValid = RequestSigningService.ValidateSignature(
            "TAMPERED123", passkey, timestamp, nonce, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateSignature_WithTamperedPasskey_ReturnsFalse()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Act - try to validate with different passkey
        var isValid = RequestSigningService.ValidateSignature(
            serverId, "WRONGKEY", timestamp, nonce, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateSignature_WithTamperedNonce_ReturnsFalse()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Act - try to validate with different nonce
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, timestamp, "tampered-nonce", signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateSignature_WithExpiredTimestamp_ReturnsFalse()
    {
        // Arrange - create signature with old timestamp
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var oldTimestamp = DateTime.UtcNow.AddSeconds(-RequestSigningService.SignatureValiditySeconds - 10);
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, oldTimestamp, nonce);

        // Act
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, oldTimestamp, nonce, signature);

        // Assert
        Assert.False(isValid, "Expired signatures should be rejected");
    }

    [Fact]
    public void ValidateSignature_WithFutureTimestamp_ReturnsFalse()
    {
        // Arrange - create signature with future timestamp (>10s ahead)
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var futureTimestamp = DateTime.UtcNow.AddSeconds(15);
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, futureTimestamp, nonce);

        // Act
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, futureTimestamp, nonce, signature);

        // Assert
        Assert.False(isValid, "Future timestamps should be rejected (clock skew protection)");
    }

    [Fact]
    public void ValidateSignature_WithRecentTimestamp_ReturnsTrue()
    {
        // Arrange - timestamp 30 seconds ago (within validity window)
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow.AddSeconds(-30);
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Act
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, timestamp, nonce, signature);

        // Assert
        Assert.True(isValid, "Recent timestamps within validity window should be accepted");
    }

    [Fact]
    public void ValidateSignature_WithInvalidBase64Signature_ReturnsFalse()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        // Act - invalid Base64 string
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, timestamp, nonce, "not-valid-base64!!!");

        // Assert
        Assert.False(isValid);
    }

    [Theory]
    [InlineData(null, "passkey", "nonce", "signature")]
    [InlineData("", "passkey", "nonce", "signature")]
    [InlineData("server", null, "nonce", "signature")]
    [InlineData("server", "", "nonce", "signature")]
    [InlineData("server", "passkey", null, "signature")]
    [InlineData("server", "passkey", "", "signature")]
    [InlineData("server", "passkey", "nonce", null)]
    [InlineData("server", "passkey", "nonce", "")]
    public void ValidateSignature_WithNullOrEmptyInputs_ReturnsFalse(
        string serverId, string passkey, string nonce, string signature)
    {
        // Arrange
        var timestamp = DateTime.UtcNow;

        // Act
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, timestamp, nonce, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateSignatureWithReason_WithValidSignature_ReturnsTrueAndNoReason()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Act
        var isValid = RequestSigningService.ValidateSignatureWithReason(
            serverId, passkey, timestamp, nonce, signature, out string reason);

        // Assert
        Assert.True(isValid);
        Assert.Empty(reason);
    }

    [Fact]
    public void ValidateSignatureWithReason_WithExpiredSignature_ReturnsFalseWithReason()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var oldTimestamp = DateTime.UtcNow.AddSeconds(-RequestSigningService.SignatureValiditySeconds - 10);
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, oldTimestamp, nonce);

        // Act
        var isValid = RequestSigningService.ValidateSignatureWithReason(
            serverId, passkey, oldTimestamp, nonce, signature, out string reason);

        // Assert
        Assert.False(isValid);
        Assert.Contains("expired", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSignatureWithReason_WithInvalidSignature_ReturnsFalseWithReason()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(serverId, passkey, timestamp, nonce);

        // Act - validate with wrong passkey
        var isValid = RequestSigningService.ValidateSignatureWithReason(
            serverId, "WRONGKEY", timestamp, nonce, signature, out string reason);

        // Assert
        Assert.False(isValid);
        Assert.Contains("mismatch", reason, StringComparison.OrdinalIgnoreCase);
    }

    // Helper method
    private static bool IsBase64String(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;

        try
        {
            Convert.FromBase64String(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
