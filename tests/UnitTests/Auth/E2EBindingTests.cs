using System.Security.Cryptography;
using System.Text.Json;
using DeskShare.Core.Auth;
using Xunit;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// End-to-end binding primitives. The fixed vector is shared with tests/js/e2e-vector.mjs: the web viewer and the
/// .NET sender must derive exactly the same keys and MACs, or they could not talk to each other.
/// </summary>
public class E2EBindingTests
{
    // Same constants as tests/js/e2e-vector.mjs
    private const string Passkey = "A3F7K9M2P";
    private const string ServerId = "server-123456789";
    private const string AuthKey = "4-ne--Ifkq6P2q0hI9jVHJ-pxGxoY8wXA0YCydFSHvE";
    private const string E2EKeyHex = "059dbe1a8b432ce2897c184963de77db35752ae20d7b7074042482ef8160d4d5";
    private const string SenderMac = "1898840887343059f95c5934f3194eaebe5aae504bdfb3f7dfe53cefefdaf1a6";
    private const string ViewerMac = "8f2c9fb1dda05ed5f38edbf5d3508486cc51cc89f03cce4aff94a2b0ea1659af";
    private static readonly string SenderFp = Fingerprint(0x00);
    private static readonly string ViewerFp = Fingerprint(0xE0);

    private static string Fingerprint(int first) =>
        string.Join(':', Enumerable.Range(first, 32).Select(b => b.ToString("X2")));

    [Fact]
    public void Hkdf_MatchesRfc5869_TestCase1()
    {
        // Proves the primitive we build on (RFC 5869 appendix A.1).
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");

        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42, salt, info);

        Assert.Equal(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Convert.ToHexString(okm).ToLowerInvariant());
    }

    [Fact]
    public void DeriveKeys_MatchesTheSharedVector()
    {
        var keys = E2EBinding.DeriveKeys(Passkey, ServerId);

        Assert.Equal(AuthKey, keys.AuthKey);
        Assert.Equal(43, keys.AuthKey.Length);
        Assert.Equal(E2EKeyHex, Convert.ToHexString(keys.E2EKey).ToLowerInvariant());
    }

    [Theory]
    [InlineData("a3f-7k9-m2p")]
    [InlineData(" A3F7K9M2P ")]
    [InlineData("a3f7k9m2p")]
    public void DeriveKeys_NormalizesWhatTheUserTyped(string typed)
    {
        Assert.Equal(AuthKey, E2EBinding.DeriveKeys(typed, ServerId).AuthKey);
    }

    [Fact]
    public void DeriveKeys_AuthAndE2EAreIndependent_AndBoundToPasskeyAndServer()
    {
        var keys = E2EBinding.DeriveKeys(Passkey, ServerId);

        Assert.NotEqual(keys.AuthKey, E2EBinding.DeriveKeys("A3F7K9M2Q", ServerId).AuthKey);
        Assert.NotEqual(keys.AuthKey, E2EBinding.DeriveKeys(Passkey, "server-123456780").AuthKey);
        // authKey (known to the signaling server) must not reveal e2eKey.
        Assert.DoesNotContain(Convert.ToBase64String(keys.E2EKey).TrimEnd('='), keys.AuthKey);
    }

    [Fact]
    public void Mac_MatchesTheSharedVector()
    {
        var key = E2EBinding.DeriveKeys(Passkey, ServerId).E2EKey;

        Assert.Equal(SenderMac, E2EBinding.ComputeMac(key, E2EBinding.SenderRole, SenderFp, ViewerFp));
        Assert.Equal(ViewerMac, E2EBinding.ComputeMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp));
    }

    [Fact]
    public void VerifyMac_AcceptsTheRightMac()
    {
        var key = E2EBinding.DeriveKeys(Passkey, ServerId).E2EKey;

        Assert.True(E2EBinding.VerifyMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp, ViewerMac));
        Assert.True(E2EBinding.VerifyMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp, ViewerMac.ToUpperInvariant()));
    }

    [Fact]
    public void VerifyMac_RejectsEverythingElse()
    {
        var key = E2EBinding.DeriveKeys(Passkey, ServerId).E2EKey;
        var otherKey = E2EBinding.DeriveKeys("WRONGKEY9", ServerId).E2EKey;

        Assert.False(E2EBinding.VerifyMac(otherKey, E2EBinding.ViewerRole, SenderFp, ViewerFp, ViewerMac)); // wrong key
        Assert.False(E2EBinding.VerifyMac(key, E2EBinding.ViewerRole, ViewerFp, SenderFp, ViewerMac));      // man in the middle: fingerprints differ
        Assert.False(E2EBinding.VerifyMac(key, E2EBinding.SenderRole, SenderFp, ViewerFp, ViewerMac));      // a mac of the other role (reflection)
        Assert.False(E2EBinding.VerifyMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp, null));
        Assert.False(E2EBinding.VerifyMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp, ""));
        Assert.False(E2EBinding.VerifyMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp, ViewerMac[..62]));
        Assert.False(E2EBinding.VerifyMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp, new string('z', 64)));
    }

    [Fact]
    public void GetFingerprint_ReadsSessionLevelLine()
    {
        var sdp = $"v=0\r\ns=-\r\na=fingerprint:sha-256 {SenderFp}\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\n";

        Assert.Equal(SenderFp, E2EBinding.GetFingerprint(sdp));
    }

    [Fact]
    public void GetFingerprint_ReadsMediaLevelLine_AndNormalizesCase()
    {
        var sdp = $"v=0\ns=-\nm=video 9 UDP/TLS/RTP/SAVPF 96\na=fingerprint:sha-256 {SenderFp.ToLowerInvariant()}\n";

        Assert.Equal(SenderFp, E2EBinding.GetFingerprint(sdp));
    }

    [Fact]
    public void GetFingerprint_AcceptsRepeatedIdenticalLines_ButNotConflictingOnes()
    {
        var same = $"a=fingerprint:sha-256 {SenderFp}\nm=audio 9 X\na=fingerprint:sha-256 {SenderFp}\nm=video 9 X\na=fingerprint:sha-256 {SenderFp}\n";
        var conflicting = $"a=fingerprint:sha-256 {SenderFp}\nm=video 9 X\na=fingerprint:sha-256 {ViewerFp}\n";

        Assert.Equal(SenderFp, E2EBinding.GetFingerprint(same));
        // Two different certificates in one SDP: we cannot tell which one DTLS will use, so there is no answer.
        Assert.Null(E2EBinding.GetFingerprint(conflicting));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v=0\r\ns=-\r\n")]
    [InlineData("a=fingerprint:sha-1 AA:BB:CC\r\n")]
    [InlineData("a=fingerprint:sha-256 AA:BB\r\n")]
    public void GetFingerprint_ReturnsNull_WhenThereIsNoUsableLine(string? sdp)
    {
        Assert.Null(E2EBinding.GetFingerprint(sdp));
    }

    [Fact]
    public void BindMessage_RoundTrips()
    {
        var json = E2EBinding.BuildBindMessage(E2EBinding.ViewerRole, ViewerMac);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("e2e_bind", doc.RootElement.GetProperty("type").GetString());
        Assert.True(E2EBinding.TryReadBind(doc.RootElement, out var role, out var mac));
        Assert.Equal(E2EBinding.ViewerRole, role);
        Assert.Equal(ViewerMac, mac);
    }

    [Theory]
    [InlineData("""{"type":"e2e_bind","role":"viewer","mac":"00"}""")]                  // no version
    [InlineData("""{"type":"e2e_bind","v":2,"role":"viewer","mac":"00"}""")]            // unknown version
    [InlineData("""{"type":"e2e_bind","v":1,"mac":"00"}""")]                           // no role
    [InlineData("""{"type":"e2e_bind","v":1,"role":"viewer","mac":5}""")]               // mac is not a string
    public void TryReadBind_RejectsMalformedMessages(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.False(E2EBinding.TryReadBind(doc.RootElement, out _, out _));
    }

    [Fact]
    public void KeyRing_VerifiesRecentKeys_AndForgetsOldOnes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ring = new E2EKeyRing(() => now);
        var first = E2EBinding.DeriveKeys("AAAAAAAAA", ServerId).E2EKey;
        var second = E2EBinding.DeriveKeys("BBBBBBBBB", ServerId).E2EKey;
        ring.Add(first, now.AddSeconds(45));
        now = now.AddSeconds(45);
        ring.Add(second, now.AddSeconds(45));

        var mac = E2EBinding.ComputeMac(first, E2EBinding.ViewerRole, SenderFp, ViewerFp);

        // The sender rotated to the second passkey, but a viewer that typed the first one is still accepted.
        Assert.True(ring.TryVerify(E2EBinding.ViewerRole, SenderFp, ViewerFp, mac, out var matched));
        Assert.Equal(first, matched);

        // A key that never verified expires with its passkey plus the grace period.
        var unusedMac = E2EBinding.ComputeMac(second, E2EBinding.ViewerRole, SenderFp, ViewerFp);
        now = now.AddSeconds(45) + E2EKeyRing.Grace + TimeSpan.FromSeconds(1);
        Assert.False(ring.TryVerify(E2EBinding.ViewerRole, SenderFp, ViewerFp, unusedMac, out _));
    }

    [Fact]
    public void KeyRing_KeepsAKeyThatVerified_ForTheViewersReconnects()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ring = new E2EKeyRing(() => now);
        var key = E2EBinding.DeriveKeys("AAAAAAAAA", ServerId).E2EKey;
        ring.Add(key, now.AddSeconds(45));
        var mac = E2EBinding.ComputeMac(key, E2EBinding.ViewerRole, SenderFp, ViewerFp);
        Assert.True(ring.TryVerify(E2EBinding.ViewerRole, SenderFp, ViewerFp, mac, out _));

        now = now.AddHours(1); // far beyond the passkey's life: the viewer reconnects with the same key

        Assert.True(ring.TryVerify(E2EBinding.ViewerRole, SenderFp, ViewerFp, mac, out _));
    }

    [Fact]
    public void KeyRing_DoesNotDuplicateTheSameKey()
    {
        var ring = new E2EKeyRing();
        var key = E2EBinding.DeriveKeys("AAAAAAAAA", ServerId).E2EKey;

        ring.Add(key, DateTime.UtcNow.AddSeconds(45));
        ring.Add((byte[])key.Clone(), DateTime.UtcNow.AddSeconds(45)); // re-registration with the same passkey

        Assert.Equal(1, ring.Count);
    }

    [Fact]
    public void KeyRing_RejectsMacOfTheWrongRoleOrFingerprints()
    {
        var ring = new E2EKeyRing();
        var key = E2EBinding.DeriveKeys("AAAAAAAAA", ServerId).E2EKey;
        ring.Add(key, DateTime.UtcNow.AddSeconds(45));
        var senderRoleMac = E2EBinding.ComputeMac(key, E2EBinding.SenderRole, SenderFp, ViewerFp);

        Assert.False(ring.TryVerify(E2EBinding.ViewerRole, SenderFp, ViewerFp, senderRoleMac, out _));
        Assert.False(ring.TryVerify(E2EBinding.SenderRole, SenderFp, SenderFp, senderRoleMac, out _));
    }
}
