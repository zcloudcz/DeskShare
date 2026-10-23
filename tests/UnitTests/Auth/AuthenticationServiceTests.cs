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
    public void GeneratePasskey_ShouldReturn9SafeCharacters()
    {
        var passkey = AuthenticationService.GeneratePasskey();

        Assert.Equal(9, passkey.Length);
        Assert.All(passkey, c => Assert.Contains(c, "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"));
    }

    [Fact]
    public void GeneratePasskey_TwoCallsProduceDifferentPasskeys()
    {
        // 32^9 possibilities: a collision here would mean the generator is not random.
        Assert.NotEqual(AuthenticationService.GeneratePasskey(), AuthenticationService.GeneratePasskey());
    }

    [Fact]
    public void GetPasskeyExpiration_IsTheFullValidityFromNow()
    {
        var timestamp = new DateTime(2025, 1, 15, 10, 30, 44, DateTimeKind.Utc); // one second before an old slot boundary

        var expiration = AuthenticationService.GetPasskeyExpiration(timestamp);

        // Valid for the full 45 s, not just until 10:30:45.
        Assert.Equal(new DateTime(2025, 1, 15, 10, 31, 29, DateTimeKind.Utc), expiration);
    }

    [Fact]
    public void PasskeyMatches_IsCaseInsensitive()
    {
        Assert.True(AuthenticationService.PasskeyMatches("A3F7K9M2P", "a3f7k9m2p"));
    }

    [Fact]
    public void PasskeyMatches_WrongOrEmptyValues_ReturnFalse()
    {
        Assert.False(AuthenticationService.PasskeyMatches("A3F7K9M2P", "WRONGPASS"));
        Assert.False(AuthenticationService.PasskeyMatches("", "A3F7K9M2P"));   // nothing registered: fail closed
        Assert.False(AuthenticationService.PasskeyMatches("A3F7K9M2P", ""));
        Assert.False(AuthenticationService.PasskeyMatches(null, null));
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
