namespace DeskShare.Unattended;

public static class AclSpec
{
    /// <summary>
    /// SDDL for %ProgramData%\DeskShare: protected DACL (parent ACEs are NOT inherited, so ProgramData's default
    /// "Users can read" does not leak the owner secret), full control for SYSTEM and Administrators only,
    /// inherited by every file and sub folder created later (OI/CI).
    /// </summary>
    public const string DataDirSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
}
