using System.Runtime.Versioning;
using System.Security.AccessControl;
using DeskShare.Unattended;

namespace DeskShare.DesktopAvalonia;

/// <summary>
/// Command-line verbs the MSI runs elevated (deferred custom action, as SYSTEM) to create / delete the machine policy:
/// <c>DeskShare.exe --write-policy signaling=wss://... remote=0 clipboard=0 files=0 privacy=0 noupdate=1</c> and <c>--remove-policy</c>.
/// Both first move the existing folder to DeskShare.Policy.bak; <c>--restore-policy</c> (MSI rollback) puts it back and
/// the MSI's commit action deletes the backup.
/// Anything that is not "0"/"1" (or an empty URL) means "not set": the user stays free to choose that option.
/// Lives in the app exe so the policy can be written even when the unattended-service files are not installed.
/// </summary>
internal static class PolicyCommand
{
    public static bool IsPolicyVerb(string[] args) => args.Length > 0 && args[0] is "--write-policy" or "--remove-policy" or "--restore-policy";

    // Directory.Delete on a junction removes the link, not its target.
    private static void DeleteDir(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            var dir = Path.GetDirectoryName(MachinePolicy.DefaultPath)!;
            var backup = dir + ".bak"; // the MSI rolls back to it (--restore-policy) or drops it on success (--commit-policy)
            if (args[0] == "--restore-policy")
            {
                DeleteDir(dir);
                if (Directory.Exists(backup)) Directory.Move(backup, dir);
                return 0;
            }
            // A standard user may have pre-created the folder (ProgramData allows that): never reuse it, recreate it with our ACL.
            DeleteDir(backup);
            if (Directory.Exists(dir)) Directory.Move(dir, backup);
            if (args[0] == "--remove-policy") return 0;

            var values = args.Skip(1).Select(a => a.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            string? Get(string key) => values.GetValueOrDefault(key);
            bool? Flag(string key) => Get(key) switch { "1" => true, "0" => false, _ => null };

            var url = Get("signaling");
            if (!string.IsNullOrEmpty(url) && !MachinePolicy.IsWebSocketUrl(url))
            {
                Console.Error.WriteLine("SIGNALING_URL must be an absolute ws:// or wss:// URL, got: " + url);
                return 2;
            }

            CreateProtectedDir(dir);
            File.WriteAllText(MachinePolicy.DefaultPath, MachinePolicy.ToJson(
                string.IsNullOrEmpty(url) ? null : url, Flag("remote"), Flag("clipboard"), Flag("files"), Flag("privacy"), Flag("noupdate") == true));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not write the DeskShare policy: " + ex.Message);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateProtectedDir(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm(AclSpec.PolicyDirSddl);
        new DirectoryInfo(dir).Create(sec);
    }
}
