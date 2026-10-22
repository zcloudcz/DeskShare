using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace DeskShare.Service;

/// <summary>Authenticode helpers: WinVerifyTrust validates the file hash + chain, the thumbprint pins WHO signed it.</summary>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    /// <summary>Thumbprint of the signer if the file carries a VALID Authenticode signature, otherwise null.</summary>
    public static string? SignerThumbprint(string path)
    {
        if (!IsValid(path)) return null;
        try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).Thumbprint; }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private static bool IsValid(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE: installs must work offline
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile, pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData;
        public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
