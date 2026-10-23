using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeskShare.Core.Auth;
using DeskShare.SignalingServer.Services;

namespace DeskShare.UnitTests.Auth;

/// <summary>Claim proof for "My devices" and the account key the server binds it to.</summary>
public class DeviceClaimProofTests
{
    // Same constants as tests/js/claim-vector.mjs: if they drift apart, a JS client's proofs are rejected.
    private const string OwnerSecret = "owner-secret-vector";
    private const string ServerId = "server-123456789";
    private const string AccountKey = "00000000-1111-2222-3333-444444444444";
    private const long Timestamp = 1792756800;
    private const string Nonce = "nonce-0123456789abcdef";
    private const string Proof = "zL6YNCE6EK+ssHWysJOtl+XSrJOfnBtajzffPDYjF8I=";

    [Fact]
    public void Compute_MatchesTheSharedVector()
    {
        Assert.Equal(Proof, DeviceClaimProof.Compute(DeviceClaimProof.ClaimKey(OwnerSecret), ServerId, AccountKey, Timestamp, Nonce));
    }

    [Fact]
    public void ClaimKey_IsTheOwnerSecretHashTheServerStores()
    {
        // ConnectionManager keeps SHA256(UTF-8 owner secret) per Server ID; the proof only works if both agree.
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(OwnerSecret)), DeviceClaimProof.ClaimKey(OwnerSecret));
    }

    [Fact]
    public void Verify_AcceptsTheProof_AndRejectsAnyChangedField()
    {
        var key = DeviceClaimProof.ClaimKey(OwnerSecret);
        Assert.True(DeviceClaimProof.Verify(key, ServerId, AccountKey, Timestamp, Nonce, Proof));

        Assert.False(DeviceClaimProof.Verify(DeviceClaimProof.ClaimKey("other"), ServerId, AccountKey, Timestamp, Nonce, Proof));
        Assert.False(DeviceClaimProof.Verify(key, "server-987654321", AccountKey, Timestamp, Nonce, Proof));
        Assert.False(DeviceClaimProof.Verify(key, ServerId, "another-account", Timestamp, Nonce, Proof));
        Assert.False(DeviceClaimProof.Verify(key, ServerId, AccountKey, Timestamp + 1, Nonce, Proof));
        Assert.False(DeviceClaimProof.Verify(key, ServerId, AccountKey, Timestamp, Nonce + "x", Proof));
        Assert.False(DeviceClaimProof.Verify(key, ServerId, AccountKey, Timestamp, Nonce, "not base64!"));
    }

    [Theory]
    [InlineData("oid-1", "sub-1", "oid-1")] // object id wins: stable across apps of the tenant
    [InlineData(null, "sub-1", "sub-1")]
    [InlineData(null, null, null)]
    [InlineData("has|pipe", null, null)]   // would break the "|"-joined proof message
    [InlineData("", "sub-1", null)]
    public void AccountKey_IsOidElseSub_AndOnlySafeCharacters(string? oid, string? sub, string? expected)
    {
        var claims = new List<Claim>();
        if (oid != null) claims.Add(new Claim("oid", oid));
        if (sub != null) claims.Add(new Claim("sub", sub));
        Assert.Equal(expected, AccountEndpoints.AccountKey(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
    }

    [Fact]
    public void Fingerprint_TellsInstallationsApart_WithoutBeingTheClaimKey()
    {
        var key = DeviceClaimProof.ClaimKey(OwnerSecret);
        var fingerprint = AccountEndpoints.Fingerprint(key);
        Assert.Equal(fingerprint, AccountEndpoints.Fingerprint(DeviceClaimProof.ClaimKey(OwnerSecret)));
        Assert.NotEqual(fingerprint, AccountEndpoints.Fingerprint(DeviceClaimProof.ClaimKey("other")));
        Assert.NotEqual(Convert.ToBase64String(key), fingerprint);
    }
}
