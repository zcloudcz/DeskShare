using System.Diagnostics;
using System.Runtime.Versioning;

namespace DeskShare.Service;

/// <summary>
/// The Windows Event Log source "DeskShare" (Application log) that the agent and the desktop app write their audit records to.
/// Creating a source needs administrator rights, which is why the elevated installer does it and a normal user app cannot
/// (that app falls back to its Serilog file log, see Core's EventLogAuditSink).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AuditEventSource
{
    // Same strings as DeskShare.Core.Safety.EventLogAuditSink.Source / LogName. Not shared on purpose: this single-file
    // installer must not reference the whole Core library just for two constants.
    private const string Source = "DeskShare";
    private const string LogName = "Application";

    // Event ids are plain numbers without a message table; the .NET Framework's resource file makes Event Viewer print the
    // text as-is instead of "The description for Event ID ... cannot be found".
    private static readonly string MessageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "EventLogMessages.dll");

    /// <summary>Best effort: a missing source only means audit records go to the file log.</summary>
    public static void Register()
    {
        try
        {
            if (EventLog.SourceExists(Source)) return;
            var data = new EventSourceCreationData(Source, LogName);
            if (File.Exists(MessageFile)) data.MessageResourceFile = MessageFile;
            EventLog.CreateEventSource(data);
            FileLog.Write("Registered the Windows Event Log source 'DeskShare' (Application log) for the session audit.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            FileLog.Write("WARNING: could not register the Event Log source, session audit goes to the file log: " + ex.Message);
        }
    }

    public static void Unregister()
    {
        try
        {
            if (EventLog.SourceExists(Source)) EventLog.DeleteEventSource(Source);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            FileLog.Write("Could not remove the Event Log source: " + ex.Message);
        }
    }
}
