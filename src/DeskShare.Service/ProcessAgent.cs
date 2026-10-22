using System.Diagnostics;
using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>Console-mode agent: a normal Process started by us, so its handle (and exit code) stays valid.</summary>
internal sealed class ProcessAgent(Process process) : IAgentProcess
{
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public void Kill() { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    public void Dispose() => process.Dispose();
}
