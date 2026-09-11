using System.Security.Cryptography;
using System.Text;
using DeskShare.Turn;

namespace DeskShare.UnitTests.Turn;

public class TurnAuthenticatorTests
{
    [Fact]
    public void Constructor_CreatesAuthenticator()
    {
        // Act
        var authenticator = new TurnAuthenticator("test.realm");

        // Assert
        Assert.NotNull(authenticator);
        Assert.Equal("test.realm", authenticator.Realm);
    }

    [Fact]
    public void AddUser_AddsUserToCredentials()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();

        // Act
        authenticator.AddUser("alice", "password123");

        // Assert
        Assert.True(authenticator.UserExists("alice"));
        Assert.Equal(1, authenticator.UserCount);
    }

    [Fact]
    public void AddUser_MultipleUsers_AllAdded()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();

        // Act
        authenticator.AddUser("alice", "pass1");
        authenticator.AddUser("bob", "pass2");
        authenticator.AddUser("charlie", "pass3");

        // Assert
        Assert.Equal(3, authenticator.UserCount);
        Assert.True(authenticator.UserExists("alice"));
        Assert.True(authenticator.UserExists("bob"));
        Assert.True(authenticator.UserExists("charlie"));
    }

    [Fact]
    public void RemoveUser_ExistingUser_Removes()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();
        authenticator.AddUser("alice", "password");

        // Act
        authenticator.RemoveUser("alice");

        // Assert
        Assert.False(authenticator.UserExists("alice"));
        Assert.Equal(0, authenticator.UserCount);
    }

    [Fact]
    public void RemoveUser_NonExistentUser_NoError()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();

        // Act & Assert
        authenticator.RemoveUser("nonexistent"); // Should not throw
        Assert.Equal(0, authenticator.UserCount);
    }

    [Fact]
    public void GenerateNonce_ReturnsBase64String()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();

        // Act
        var nonce = authenticator.GenerateNonce();

        // Assert
        Assert.NotNull(nonce);
        Assert.NotEmpty(nonce);

        // Should be valid base64
        var bytes = Convert.FromBase64String(nonce);
        Assert.Equal(16, bytes.Length);
    }

    [Fact]
    public void GenerateNonce_MultipleCalls_ReturnsDifferentValues()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();

        // Act
        var nonce1 = authenticator.GenerateNonce();
        var nonce2 = authenticator.GenerateNonce();
        var nonce3 = authenticator.GenerateNonce();

        // Assert
        Assert.NotEqual(nonce1, nonce2);
        Assert.NotEqual(nonce2, nonce3);
        Assert.NotEqual(nonce1, nonce3);
    }

    [Fact]
    public void ComputeMessageIntegrity_ValidUser_ReturnsHash()
    {
        // Arrange
        var authenticator = new TurnAuthenticator("test.realm");
        authenticator.AddUser("alice", "password");
        var message = Encoding.UTF8.GetBytes("test message");

        // Act
        var hash = authenticator.ComputeMessageIntegrity("alice", message);

        // Assert
        Assert.NotNull(hash);
        Assert.Equal(20, hash.Length); // HMAC-SHA1 is 20 bytes
    }

    [Fact]
    public void ComputeMessageIntegrity_NonExistentUser_ThrowsException()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();
        var message = Encoding.UTF8.GetBytes("test");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            authenticator.ComputeMessageIntegrity("nonexistent", message));
    }

    [Fact]
    public void ValidateMessageIntegrity_CorrectHash_ReturnsTrue()
    {
        // Arrange
        var authenticator = new TurnAuthenticator("test.realm");
        authenticator.AddUser("alice", "password");
        var message = Encoding.UTF8.GetBytes("test message");
        var correctHash = authenticator.ComputeMessageIntegrity("alice", message);

        // Act
        var isValid = authenticator.ValidateMessageIntegrity("alice", correctHash, message);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateMessageIntegrity_IncorrectHash_ReturnsFalse()
    {
        // Arrange
        var authenticator = new TurnAuthenticator("test.realm");
        authenticator.AddUser("alice", "password");
        var message = Encoding.UTF8.GetBytes("test message");
        var wrongHash = new byte[20]; // All zeros

        // Act
        var isValid = authenticator.ValidateMessageIntegrity("alice", wrongHash, message);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateMessageIntegrity_NonExistentUser_ReturnsFalse()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();
        var hash = new byte[20];
        var message = Encoding.UTF8.GetBytes("test");

        // Act
        var isValid = authenticator.ValidateMessageIntegrity("nonexistent", hash, message);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateMessageIntegrity_DifferentMessage_ReturnsFalse()
    {
        // Arrange
        var authenticator = new TurnAuthenticator("test.realm");
        authenticator.AddUser("alice", "password");
        var message1 = Encoding.UTF8.GetBytes("message1");
        var message2 = Encoding.UTF8.GetBytes("message2");
        var hash = authenticator.ComputeMessageIntegrity("alice", message1);

        // Act
        var isValid = authenticator.ValidateMessageIntegrity("alice", hash, message2);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void UserExists_ExistingUser_ReturnsTrue()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();
        authenticator.AddUser("alice", "password");

        // Act
        var exists = authenticator.UserExists("alice");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public void UserExists_NonExistentUser_ReturnsFalse()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();

        // Act
        var exists = authenticator.UserExists("alice");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void UserCount_InitiallyZero()
    {
        // Arrange
        var authenticator = new TurnAuthenticator();

        // Act & Assert
        Assert.Equal(0, authenticator.UserCount);
    }

    [Fact]
    public void ComputeMessageIntegrity_SameInputs_ReturnsSameHash()
    {
        // Arrange
        var authenticator = new TurnAuthenticator("test.realm");
        authenticator.AddUser("alice", "password");
        var message = Encoding.UTF8.GetBytes("test message");

        // Act
        var hash1 = authenticator.ComputeMessageIntegrity("alice", message);
        var hash2 = authenticator.ComputeMessageIntegrity("alice", message);

        // Assert
        Assert.Equal(hash1, hash2);
    }
}
