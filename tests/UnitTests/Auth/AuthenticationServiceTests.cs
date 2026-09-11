using DeskShare.Core.Auth;
using Xunit;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Unit tests for AuthenticationService.
/// Tests passkey generation, validation, and formatting.
/// </summary>
public class AuthenticationServiceTests
{
    [Fact]
    public void GenerateServerId_ShouldReturnNonEmptyString()
    {
        // Act
        var serverId = AuthenticationService.GenerateServerId();

        // Assert
        Assert.False(string.IsNullOrEmpty(serverId));
        Assert.StartsWith("server-", serverId); // Format: "server-{base64hash}"
    }

    [Fact]
    public void GeneratePasskey_ShouldReturn9Characters()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp = DateTime.UtcNow;

        // Act
        var passkey = AuthenticationService.GeneratePasskey(serverId, timestamp);

        // Assert
        Assert.Equal(9, passkey.Length);
    }

    [Fact]
    public void GeneratePasskey_ShouldOnlyUseSafeCharacters()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp = DateTime.UtcNow;

        // Act
        var passkey = AuthenticationService.GeneratePasskey(serverId, timestamp);

        // Assert - should not contain ambiguous characters (0, O, 1, I)
        Assert.DoesNotContain("0", passkey);
        Assert.DoesNotContain("O", passkey);
        Assert.DoesNotContain("1", passkey);
        Assert.DoesNotContain("I", passkey);
    }

    [Fact]
    public void GeneratePasskey_SameTimestampShouldProduceSamePasskey()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp = new DateTime(2025, 1, 15, 10, 30, 25, DateTimeKind.Utc);

        // Act
        var passkey1 = AuthenticationService.GeneratePasskey(serverId, timestamp);
        var passkey2 = AuthenticationService.GeneratePasskey(serverId, timestamp);

        // Assert
        Assert.Equal(passkey1, passkey2);
    }

    [Fact]
    public void GeneratePasskey_SameIntervalShouldProduceSamePasskey()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp1 = new DateTime(2025, 1, 15, 10, 30, 10, DateTimeKind.Utc); // 10:30:10
        var timestamp2 = new DateTime(2025, 1, 15, 10, 30, 40, DateTimeKind.Utc); // 10:30:40 (same 45s interval)

        // Act
        var passkey1 = AuthenticationService.GeneratePasskey(serverId, timestamp1);
        var passkey2 = AuthenticationService.GeneratePasskey(serverId, timestamp2);

        // Assert - both timestamps are in the 10:30:00-10:30:44 interval
        Assert.Equal(passkey1, passkey2);
    }

    [Fact]
    public void GeneratePasskey_DifferentIntervalShouldProduceDifferentPasskey()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp1 = new DateTime(2025, 1, 15, 10, 30, 10, DateTimeKind.Utc); // 10:30:10
        var timestamp2 = new DateTime(2025, 1, 15, 10, 31, 0, DateTimeKind.Utc);  // 10:31:00 (different interval)

        // Act
        var passkey1 = AuthenticationService.GeneratePasskey(serverId, timestamp1);
        var passkey2 = AuthenticationService.GeneratePasskey(serverId, timestamp2);

        // Assert
        Assert.NotEqual(passkey1, passkey2);
    }

    [Fact]
    public void GeneratePasskey_DifferentServerIdShouldProduceDifferentPasskey()
    {
        // Arrange
        var serverId1 = "A1B2C3D4E5F6";
        var serverId2 = "F6E5D4C3B2A1";
        var timestamp = DateTime.UtcNow;

        // Act
        var passkey1 = AuthenticationService.GeneratePasskey(serverId1, timestamp);
        var passkey2 = AuthenticationService.GeneratePasskey(serverId2, timestamp);

        // Assert
        Assert.NotEqual(passkey1, passkey2);
    }

    [Fact]
    public void GetPasskeyExpiration_ShouldReturn45SecondsFromIntervalStart()
    {
        // Arrange
        var timestamp = new DateTime(2025, 1, 15, 10, 30, 25, DateTimeKind.Utc); // 10:30:25

        // Act
        var expiration = AuthenticationService.GetPasskeyExpiration(timestamp);

        // Assert
        // 10:30:25 is in interval 10:30:00-10:30:44, so expiration should be 10:30:45
        var expectedExpiration = new DateTime(2025, 1, 15, 10, 30, 45, DateTimeKind.Utc);
        Assert.Equal(expectedExpiration, expiration);
    }

    [Fact]
    public void ValidatePasskey_CorrectPasskeyShouldReturnTrue()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp = DateTime.UtcNow;
        var passkey = AuthenticationService.GeneratePasskey(serverId, timestamp);

        // Act
        var isValid = AuthenticationService.ValidatePasskey(serverId, passkey, timestamp);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidatePasskey_IncorrectPasskeyShouldReturnFalse()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp = DateTime.UtcNow;

        // Act
        var isValid = AuthenticationService.ValidatePasskey(serverId, "WRONGPASS", timestamp);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidatePasskey_ShouldBeCaseInsensitive()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp = DateTime.UtcNow;
        var passkey = AuthenticationService.GeneratePasskey(serverId, timestamp);

        // Act - validate with lowercase version
        var isValid = AuthenticationService.ValidatePasskey(serverId, passkey.ToLowerInvariant(), timestamp);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidatePasskey_ExpiredPasskeyShouldReturnFalse()
    {
        // Arrange
        var serverId = "A1B2C3D4E5F6";
        var timestamp1 = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var passkey = AuthenticationService.GeneratePasskey(serverId, timestamp1);

        // Act - validate with timestamp 1 minute later (different interval)
        var timestamp2 = timestamp1.AddMinutes(1);
        var isValid = AuthenticationService.ValidatePasskey(serverId, passkey, timestamp2);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void FormatPasskeyForDisplay_ShouldAddDashes()
    {
        // Arrange
        var passkey = "A3F7K9M2P";

        // Act
        var formatted = AuthenticationService.FormatPasskeyForDisplay(passkey);

        // Assert
        Assert.Equal("A3F-7K9-M2P", formatted);
    }

    [Fact]
    public void UnformatPasskey_ShouldRemoveDashes()
    {
        // Arrange
        var formatted = "A3F-7K9-M2P";

        // Act
        var passkey = AuthenticationService.UnformatPasskey(formatted);

        // Assert
        Assert.Equal("A3F7K9M2P", passkey);
    }

    [Fact]
    public void UnformatPasskey_ShouldConvertToUppercase()
    {
        // Arrange
        var formatted = "a3f-7k9-m2p";

        // Act
        var passkey = AuthenticationService.UnformatPasskey(formatted);

        // Assert
        Assert.Equal("A3F7K9M2P", passkey);
    }
}
