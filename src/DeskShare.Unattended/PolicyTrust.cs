using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeskShare.Unattended;

/// <summary>
/// Decides whether the policy folder / file can be believed: owned by SYSTEM or Administrators, no junction or symlink, and
/// nobody except SYSTEM / Administrators holds a write-type right (the folder's DACL must also be protected, so a parent
/// cannot add one later). Same idea as Installer.PrepareDataDir, but readers only need "not writable by non-admins".
/// On non-Windows there is no policy file at all, so nothing to check.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PolicyTrust
{
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x40000000 /* GENERIC_WRITE */ | (FileSystemRights)0x10000000 /* GENERIC_ALL */;

    /// <summary>Null = trusted, otherwise the reason. Checks the folder, and the file when it exists.</summary>
    public static string? Problem(string policyFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(policyFile))!;
            var folder = FolderProblem(dir);
            if (folder != null || !File.Exists(policyFile)) return folder;
            var info = new FileInfo(policyFile);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "policy file is a link";
            var sec = info.GetAccessControl();
            return AclProblem(sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier, false, sec.AreAccessRulesProtected,
                sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "cannot verify permissions: " + ex.Message;
        }
    }

    public static string? FolderProblem(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return null;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "policy folder is a junction or link";
        var sec = info.GetAccessControl();
        return AclProblem(sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier, true, sec.AreAccessRulesProtected,
            sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
    }

    /// <summary>The pure rule, separated so it can be unit-tested with hand-made rules.</summary>
    public static string? AclProblem(SecurityIdentifier? owner, bool requireProtected, bool isProtected, IEnumerable<FileSystemAccessRule> rules)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (owner == null || (!owner.Equals(system) && !owner.Equals(admins))) return $"owner is {owner?.Value ?? "unknown"}, not SYSTEM/Administrators";
        if (requireProtected && !isProtected) return "permissions are inherited from the parent folder";
        foreach (var r in rules)
        {
            if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & WriteRights) == 0) continue;
            var who = r.IdentityReference;
            if (!who.Equals(system) && !who.Equals(admins)) return $"{who.Value} has write access";
        }
        return null;
    }
}
