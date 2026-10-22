using System.ComponentModel;
using DeskShare.Unattended;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskShare.Service;

/// <summary>
/// Starts a process with the service's own SYSTEM token moved into another session, on that session's interactive
/// desktop. This is how a session-0 service reaches a user's screen (same principle as RustDesk / TeamViewer).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SessionLauncher
{
    private const uint TOKEN_DUPLICATE = 0x2, TOKEN_QUERY = 0x8, TOKEN_ASSIGN_PRIMARY = 0x1, TOKEN_ADJUST_SESSIONID = 0x100;
    private const int SecurityImpersonation = 2, TokenPrimary = 1, TokenSessionId = 12;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;

    public static uint ActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    /// <summary>Runs <paramref name="exe"/> with <paramref name="arguments"/> in <paramref name="sessionId"/> as SYSTEM on winsta0\default.</summary>
    public static IAgentProcess Start(uint sessionId, string exe, string arguments)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_SESSIONID, out var own))
            throw new Win32Exception();
        IntPtr token = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(own, 0xF01FF /* TOKEN_ALL_ACCESS */, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out token))
                throw new Win32Exception();
            var sid = sessionId;
            if (!SetTokenInformation(token, TokenSessionId, ref sid, sizeof(uint)))
                throw new Win32Exception();
            if (!CreateEnvironmentBlock(out env, token, false))
                env = IntPtr.Zero; // fall back to inheriting the service environment

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = $"\"{exe}\" {arguments}";
            if (!CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(exe), ref si, out var pi))
                throw new Win32Exception();
            CloseHandle(pi.hThread);
            // Keep hProcess: the supervisor reads the exit code after the process is gone (a PID lookup would throw by then).
            return new HandleAgent(pi.hProcess, pi.dwProcessId);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(own);
        }
    }

    private sealed class HandleAgent(IntPtr handle, int pid) : IAgentProcess
    {
        private IntPtr _handle = handle;
        public int Id => pid;
        public bool HasExited => _handle == IntPtr.Zero || WaitForSingleObject(_handle, 0) == 0; // WAIT_OBJECT_0
        public int ExitCode => GetExitCodeProcess(_handle, out var code) ? (int)code : -1;
        public void Kill() { if (_handle != IntPtr.Zero) TerminateProcess(_handle, 1); }
        public void Dispose() { if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; } }
    }

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
}
