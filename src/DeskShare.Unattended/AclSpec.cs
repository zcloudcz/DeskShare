namespace DeskShare.Unattended;

public static class AclSpec
{
    /// <summary>
    /// SDDL for %ProgramData%\DeskShare: protected DACL (parent ACEs are NOT inherited, so ProgramData's default
    /// "Users can read" does not leak the owner secret), full control for SYSTEM and Administrators only,
    /// inherited by every file and sub folder created later (OI/CI).
    /// </summary>
    public const string DataDirSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    /// <summary>
    /// SDDL for %ProgramData%\DeskShare.Policy (the MSI's policy.json): protected DACL, full control for SYSTEM and
    /// Administrators, read + traverse for Users, so the user's app can read the policy but not change it.
    /// </summary>
    public const string PolicyDirSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";
}
