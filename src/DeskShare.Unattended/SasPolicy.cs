namespace DeskShare.Unattended;

/// <summary>Ownership claim on the SoftwareSASGeneration value. Pending = we are about to write (or just wrote) it.</summary>
public enum SasMarker { None, Pending, Done }

/// <summary>
/// The registry side of <see cref="SasPolicy"/>, so the decisions can be unit-tested with a fake.
/// Real implementation: HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\SoftwareSASGeneration plus a marker
/// under HKLM\SOFTWARE\DeskShare (HKLM is admin-only, unlike the user-creatable ProgramData folder).
/// </summary>
public interface ISasPolicyStore
{
    int? ReadSoftwareSasGeneration();
    void WriteSoftwareSasGeneration(int value);
    void DeleteSoftwareSasGeneration();

    /// <summary>Whether our installer (and not the admin or other software) created the SoftwareSASGeneration value.</summary>
    SasMarker Marker { get; set; }
}

/// <summary>
/// Windows only lets a service send Ctrl+Alt+Del (SendSAS) when SoftwareSASGeneration is 1 (services) or 3 (services and
/// ease-of-access apps). The installer enables it only if nobody configured it, and the uninstaller undoes exactly that.
/// The marker is written BEFORE the value so a crash in between can never leave a value we would not remove again.
/// </summary>
public static class SasPolicy
{
    public const int ServicesAllowed = 1;

    /// <returns>True when the value was created by this call. Throws (after reverting) when the registry refuses a write.</returns>
    public static bool EnableForServices(ISasPolicyStore store)
    {
        ResolvePending(store);
        // Any existing value (even 0) is an explicit choice of the admin and stays untouched.
        if (store.ReadSoftwareSasGeneration() != null)
            return false;

        store.Marker = SasMarker.Pending; // if this throws, nothing was written yet
        try
        {
            store.WriteSoftwareSasGeneration(ServicesAllowed);
            store.Marker = SasMarker.Done;
        }
        catch
        {
            Revert(store);
            throw;
        }
        return true;
    }

    /// <returns>True when the value was removed.</returns>
    public static bool RestoreOnUninstall(ISasPolicyStore store)
    {
        if (store.Marker == SasMarker.None)
            return false;
        // If someone changed it since, it is theirs now: leave it, only forget our claim.
        var remove = store.ReadSoftwareSasGeneration() == ServicesAllowed;
        if (remove)
            store.DeleteSoftwareSasGeneration();
        store.Marker = SasMarker.None;
        return remove;
    }

    // A Pending marker left by an interrupted run: if our value is there the claim is complete, otherwise drop it.
    private static void ResolvePending(ISasPolicyStore store)
    {
        if (store.Marker != SasMarker.Pending)
            return;
        store.Marker = store.ReadSoftwareSasGeneration() == ServicesAllowed ? SasMarker.Done : SasMarker.None;
    }

    private static void Revert(ISasPolicyStore store)
    {
        // Never leave the value without a claim: drop the marker only once the value is confirmed gone. If reading or deleting
        // fails, the marker is left as Pending so the next install resolves it and uninstall still removes the value.
        try
        {
            if (store.ReadSoftwareSasGeneration() == ServicesAllowed) store.DeleteSoftwareSasGeneration();
            if (store.ReadSoftwareSasGeneration() == ServicesAllowed) { TryMarkPending(store); return; }
        }
        catch { TryMarkPending(store); return; }
        try { store.Marker = SasMarker.None; } catch { }
    }

    private static void TryMarkPending(ISasPolicyStore store)
    {
        try { store.Marker = SasMarker.Pending; } catch { }
    }
}
