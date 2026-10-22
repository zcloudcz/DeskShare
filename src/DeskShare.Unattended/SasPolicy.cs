namespace DeskShare.Unattended;

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

    /// <summary>True when our installer (and not the admin or other software) created the SoftwareSASGeneration value.</summary>
    bool InstallerSetIt { get; set; }
}

/// <summary>
/// Windows only lets a service send Ctrl+Alt+Del (SendSAS) when SoftwareSASGeneration is 1 (services) or 3 (services and
/// ease-of-access apps). The installer enables it only if nobody configured it, and the uninstaller undoes exactly that.
/// </summary>
public static class SasPolicy
{
    public const int ServicesAllowed = 1;

    /// <returns>True when the value was created by this call.</returns>
    public static bool EnableForServices(ISasPolicyStore store)
    {
        // Any existing value (even 0) is an explicit choice of the admin and stays untouched.
        if (store.ReadSoftwareSasGeneration() != null)
            return false;
        store.WriteSoftwareSasGeneration(ServicesAllowed);
        store.InstallerSetIt = true;
        return true;
    }

    /// <returns>True when the value was removed.</returns>
    public static bool RestoreOnUninstall(ISasPolicyStore store)
    {
        if (!store.InstallerSetIt)
            return false;
        // If someone changed it since, it is theirs now: leave it, only forget our claim.
        var remove = store.ReadSoftwareSasGeneration() == ServicesAllowed;
        if (remove)
            store.DeleteSoftwareSasGeneration();
        store.InstallerSetIt = false;
        return remove;
    }
}
