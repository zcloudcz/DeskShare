namespace DeskShare.Core.Auth;

/// <summary>
/// The sender's e2e keys, one per passkey it has issued recently (see <see cref="E2EBinding"/>).
/// </summary>
/// <remarks>
/// Why a list and not just the current key: the passkey rotates every 45 s. A viewer that authenticated with the
/// previous passkey is still connecting when the sender rotates, so the sender must still accept that key for a while.
/// A key that already verified a viewer is kept longer, because that viewer reconnects (resume token, trusted
/// client) with the SAME key and the passkey it typed is long gone. Everything lives in memory only.
/// </remarks>
public sealed class E2EKeyRing
{
    /// <summary>How long a key outlives its passkey: enough for slow ICE/DTLS plus the 10 s binding deadline.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    /// <summary>How long a key that verified a viewer is kept for that viewer's reconnects.</summary>
    public static readonly TimeSpan VerifiedRetention = TimeSpan.FromHours(8);

    private readonly object _lock = new();
    private readonly List<(byte[] Key, DateTime ExpiresUtc)> _keys = new();
    private readonly Func<DateTime> _utcNow;

    public E2EKeyRing(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Adds the key of a passkey that is valid until <paramref name="passkeyValidToUtc"/>.</summary>
    public void Add(byte[] e2eKey, DateTime passkeyValidToUtc)
    {
        lock (_lock)
        {
            Prune();
            var expires = passkeyValidToUtc + Grace;

            // Re-registration with the same passkey (signaling reconnect) adds the same key again: refresh, don't duplicate.
            var existing = _keys.FindIndex(k => k.Key.AsSpan().SequenceEqual(e2eKey));
            if (existing >= 0)
                _keys[existing] = (e2eKey, expires > _keys[existing].ExpiresUtc ? expires : _keys[existing].ExpiresUtc);
            else
                _keys.Add((e2eKey, expires));
        }
    }

    /// <summary>
    /// Finds the key that makes <paramref name="mac"/> a valid bind of <paramref name="role"/> over the two
    /// fingerprints. Every key is tried (no early exit on timing-sensitive data: the compare itself is constant time).
    /// A match is kept for <see cref="VerifiedRetention"/>.
    /// </summary>
    public bool TryVerify(string role, string senderFingerprint, string viewerFingerprint, string mac, out byte[] matchedKey)
    {
        lock (_lock)
        {
            Prune();
            matchedKey = Array.Empty<byte>();
            var matched = -1;
            for (var i = 0; i < _keys.Count; i++)
            {
                if (E2EBinding.VerifyMac(_keys[i].Key, role, senderFingerprint, viewerFingerprint, mac) && matched < 0)
                    matched = i;
            }

            if (matched < 0) return false;

            matchedKey = _keys[matched].Key;
            var keep = _utcNow() + VerifiedRetention;
            if (keep > _keys[matched].ExpiresUtc)
                _keys[matched] = (matchedKey, keep);
            return true;
        }
    }

    /// <summary>Number of keys currently held; for tests.</summary>
    internal int Count
    {
        get { lock (_lock) { Prune(); return _keys.Count; } }
    }

    private void Prune()
    {
        var now = _utcNow();
        _keys.RemoveAll(k => k.ExpiresUtc <= now);
    }
}
