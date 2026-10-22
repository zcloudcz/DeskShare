using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using DeskShare.Unattended;
using Microsoft.Win32;

namespace DeskShare.DesktopAvalonia;

/// <summary>
/// Command-line verbs the MSI runs elevated (deferred custom action, as SYSTEM) to create / delete the machine policy:
/// <c>DeskShare.exe --write-policy txn=ID en=0 sig=wss://... rc=0 cb=0 fl=0 pv=0 nu=1</c>,
/// <c>--remove-policy txn=ID</c> and, from the MSI rollback, <c>--restore-policy txn=ID</c>.
/// Anything that is not "0"/"1" (or an empty URL) means "not set": the user stays free to choose that option.
/// Besides policy.json the write also stores the raw MSI property values in HKLM\SOFTWARE\DeskShare\Msi, where the next
/// repair / upgrade finds them again (written here, not by a component, so they follow EVERY run, also in maintenance mode).
///
/// Transaction safety: write / remove first create DeskShare.Policy.bak (marked with the MSI transaction id) holding the previous
/// folder ("prev") and registry values. <c>--restore-policy</c> acts only on a backup carrying ITS transaction id, so a rollback
/// that runs before the write happened (or beside a stale backup) never touches the live policy. If the backup cannot be
/// prepared the write aborts before the live folder is touched. The MSI's commit action deletes the backup.
/// Lives in the app exe so the policy can be written even when the unattended-service files are not installed.
/// </summary>
internal static class PolicyCommand
{
    private const string RegKey = @"SOFTWARE\DeskShare\Msi";

    public static bool IsPolicyVerb(string[] args) => args.Length > 0 && args[0] is "--write-policy" or "--remove-policy" or "--restore-policy";

    // Directory.Delete on a junction removes the link, not its target.
    private static void DeleteDir(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            var dir = Path.GetDirectoryName(MachinePolicy.DefaultPath)!;
            var backup = dir + ".bak";
            var values = args.Skip(1).Select(a => a.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            string? Get(string key) => values.GetValueOrDefault(key);
            var txn = Get("txn") ?? "";
            if (txn.Length == 0) return 2;
            if (args[0] == "--restore-policy") return Restore(dir, backup, txn);

            var url = Get("sig");
            if (args[0] == "--write-policy" && !string.IsNullOrEmpty(url) && !MachinePolicy.IsWebSocketUrl(url))
            {
                Console.Error.WriteLine("SIGNALING_URL must be an absolute ws:// or wss:// URL, got: " + url);
                return 2; // nothing touched yet
            }

            Backup(dir, backup, txn);
            if (args[0] == "--remove-policy")
            {
                Registry.LocalMachine.DeleteSubKeyTree(RegKey, false);
                return 0;
            }

            bool? Flag(string key) => Get(key) switch { "1" => true, "0" => false, _ => null };
            CreateProtectedDir(dir);
            File.WriteAllText(MachinePolicy.DefaultPath, MachinePolicy.ToJson(
                string.IsNullOrEmpty(url) ? null : url, Flag("rc"), Flag("cb"), Flag("fl"), Flag("pv"), Flag("nu") == true));

            using var key = Registry.LocalMachine.CreateSubKey(RegKey);
            foreach (var (name, arg) in RememberedValues) key.SetValue(name, Get(arg) ?? "");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not write the DeskShare policy: " + ex.Message);
            return 1;
        }
    }

    /// <summary>MSI property name stored in the registry -> command-line argument carrying it.</summary>
    private static readonly (string Name, string Arg)[] RememberedValues =
    {
        ("SIGNALING_URL", "sig"), ("ENABLE_UNATTENDED", "en"), ("ALLOW_REMOTE_CONTROL", "rc"),
        ("ALLOW_CLIPBOARD", "cb"), ("ALLOW_FILES", "fl"), ("ALLOW_PRIVACY_SCREEN", "pv"), ("DISABLE_AUTOUPDATE", "nu"),
    };

    /// <summary>Any failure here (e.g. a stale backup that cannot be deleted) propagates BEFORE the live folder is moved.</summary>
    [SupportedOSPlatform("windows")]
    private static void Backup(string dir, string backup, string txn)
    {
        DeleteDir(backup);
        Directory.CreateDirectory(backup);
        using (var key = Registry.LocalMachine.OpenSubKey(RegKey))
        {
            if (key != null)
                File.WriteAllText(Path.Combine(backup, "registry.json"),
                    JsonSerializer.Serialize(key.GetValueNames().ToDictionary(n => n, n => key.GetValue(n)?.ToString() ?? "")));
        }
        File.WriteAllText(Path.Combine(backup, "txn.txt"), txn); // marks the backup as made by THIS transaction
        if (Directory.Exists(dir)) Directory.Move(dir, Path.Combine(backup, "prev"));
    }

    [SupportedOSPlatform("windows")]
    private static int Restore(string dir, string backup, string txn)
    {
        var marker = Path.Combine(backup, "txn.txt");
        if (!File.Exists(marker) || File.ReadAllText(marker) != txn) return 0; // not our backup: leave the live policy alone
        DeleteDir(dir);
        var prev = Path.Combine(backup, "prev");
        if (Directory.Exists(prev)) Directory.Move(prev, dir);
        Registry.LocalMachine.DeleteSubKeyTree(RegKey, false);
        var saved = Path.Combine(backup, "registry.json");
        if (File.Exists(saved))
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKey);
            foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(saved))!) key.SetValue(name, value);
        }
        DeleteDir(backup);
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static void CreateProtectedDir(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm(AclSpec.PolicyDirSddl);
        new DirectoryInfo(dir).Create(sec);
    }
}
