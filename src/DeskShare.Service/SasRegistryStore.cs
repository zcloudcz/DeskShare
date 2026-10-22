using System.Runtime.Versioning;
using DeskShare.Unattended;
using Microsoft.Win32;

namespace DeskShare.Service;

/// <summary>Elevated installer only (writes HKLM). 64-bit view explicitly, so a 32-bit helper would hit the same keys.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SasRegistryStore : ISasPolicyStore
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "SoftwareSASGeneration";
    private const string OurKey = @"SOFTWARE\DeskShare";
    private const string MarkerName = "SasGenerationSetByInstaller";

    private static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public int? ReadSoftwareSasGeneration()
    {
        using var key = Hklm.OpenSubKey(PolicyKey);
        return key?.GetValue(ValueName) as int?;
    }

    public void WriteSoftwareSasGeneration(int value)
    {
        using var key = Hklm.CreateSubKey(PolicyKey);
        key.SetValue(ValueName, value, RegistryValueKind.DWord);
    }

    public void DeleteSoftwareSasGeneration()
    {
        using var key = Hklm.OpenSubKey(PolicyKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public bool InstallerSetIt
    {
        get
        {
            using var key = Hklm.OpenSubKey(OurKey);
            return key?.GetValue(MarkerName) as int? == 1;
        }
        set
        {
            if (value)
            {
                using var key = Hklm.CreateSubKey(OurKey);
                key.SetValue(MarkerName, 1, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Hklm.OpenSubKey(OurKey, writable: true);
                key?.DeleteValue(MarkerName, throwOnMissingValue: false);
            }
        }
    }
}
