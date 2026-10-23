using System.Diagnostics;
using System.Text;
using Serilog;

namespace DeskShare.Core.Safety;

/// <summary>What happened. The numeric value is the Windows Event Log event id, so keep the values stable.</summary>
public enum AuditKind
{
    SessionStarted = 100,
    SessionEnded = 101,
    ControlGranted = 110,
    ControlDenied = 111,
    ControlRevoked = 112,
    PrivacyScreenOn = 120,
    PrivacyScreenOff = 121,
    FileReceived = 130,
    ClipboardTransfer = 140,
    DisplaySwitched = 150,
}

/// <summary>One audit record. <see cref="Text"/> is already formatted and never holds content (clipboard text, keystrokes).</summary>
public readonly record struct AuditEvent(AuditKind Kind, string Text)
{
    public int EventId => (int)Kind;
}

/// <summary>
/// Builds the audit records. Everything the viewer controls (file names) is cleaned first, because it ends up in
/// a log that an administrator reads. Only metadata goes in: ids, addresses, names, sizes, never content.
/// </summary>
public static class AuditEvents
{
    public static AuditEvent SessionStarted(string sessionId, string? remoteAddress, bool endToEndVerified) =>
        new(AuditKind.SessionStarted,
            $"Remote session {sessionId} started. Viewer address: {remoteAddress ?? "unknown"}. End-to-end verified: {(endToEndVerified ? "yes" : "no")}.");

    public static AuditEvent SessionEnded(string sessionId) =>
        new(AuditKind.SessionEnded, $"Remote session {sessionId} ended.");

    public static AuditEvent ControlGranted() => new(AuditKind.ControlGranted, "Remote control of mouse and keyboard was granted.");

    public static AuditEvent ControlDenied(string reason) => new(AuditKind.ControlDenied, $"Remote control was not granted: {Clean(reason)}.");

    public static AuditEvent ControlRevoked() => new(AuditKind.ControlRevoked, "Remote control was revoked.");

    public static AuditEvent PrivacyScreen(bool on, string? reason) =>
        new(on ? AuditKind.PrivacyScreenOn : AuditKind.PrivacyScreenOff,
            on ? "Privacy screen turned on." : $"Privacy screen turned off{(reason == null ? "" : ": " + Clean(reason))}.");

    public static AuditEvent FileReceived(string name, long sizeBytes) =>
        new(AuditKind.FileReceived, $"File received from the viewer: {Clean(name)} ({sizeBytes} bytes).");

    /// <param name="toHost">True when the viewer sent text to this computer, false for the other direction.</param>
    public static AuditEvent ClipboardTransfer(bool toHost, long sizeBytes) =>
        new(AuditKind.ClipboardTransfer, $"Clipboard text transferred {(toHost ? "from the viewer to this computer" : "from this computer to the viewer")} ({sizeBytes} bytes).");

    public static AuditEvent DisplaySwitched(string displayName) =>
        new(AuditKind.DisplaySwitched, $"The viewer switched the shared display to: {Clean(displayName)}.");

    /// <summary>Drops control characters (log injection, fake extra lines) and caps the length.</summary>
    internal static string Clean(string? value, int max = 120)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (var c in value)
        {
            if (sb.Length >= max) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }
}

public interface IAuditSink
{
    void Write(AuditEvent auditEvent);
}

/// <summary>
/// Writes to the Windows Event Log (source "DeskShare" in the Application log). The source is registered by the
/// elevated service installer; a normal user app cannot create it. Without it the records go to the Serilog file log
/// instead, and auditing never throws into the caller.
/// </summary>
public sealed class EventLogAuditSink : IAuditSink
{
    public const string Source = "DeskShare";
    public const string LogName = "Application";

    private readonly Func<bool> _sourceExists;
    private readonly Action<AuditEvent> _writeEventLog;
    private readonly Action<AuditEvent> _fallback;
    private bool? _useEventLog; // decided once: checking the source touches the registry

    public EventLogAuditSink() : this(SourceExistsOnThisMachine, WriteToEventLog, WriteToSerilog) { }

    /// <summary>Lets tests replace the three Windows/Serilog touch points.</summary>
    internal EventLogAuditSink(Func<bool> sourceExists, Action<AuditEvent> writeEventLog, Action<AuditEvent> fallback)
    {
        _sourceExists = sourceExists;
        _writeEventLog = writeEventLog;
        _fallback = fallback;
    }

    public void Write(AuditEvent auditEvent)
    {
        try
        {
            _useEventLog ??= SafeSourceExists();
            if (_useEventLog == true)
            {
                _writeEventLog(auditEvent);
                return;
            }
        }
        catch (Exception)
        {
            _useEventLog = false; // e.g. the log is full or access was denied: stay on the file log from now on
        }

        try { _fallback(auditEvent); }
        catch (Exception) { /* auditing must never break a remote session */ }
    }

    private bool SafeSourceExists()
    {
        try { return _sourceExists(); }
        catch (Exception) { return false; } // SourceExists can throw SecurityException for a limited user
    }

    private static bool SourceExistsOnThisMachine() =>
        OperatingSystem.IsWindows() && EventLog.SourceExists(Source);

    private static void WriteToEventLog(AuditEvent e)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Event ids are the AuditKind values; category 0.
        EventLog.WriteEntry(Source, e.Text, EventLogEntryType.Information, e.EventId);
    }

    private static void WriteToSerilog(AuditEvent e) =>
        Log.Information("AUDIT {Kind} ({EventId}): {Text}", e.Kind, e.EventId, e.Text);
}

/// <summary>The process-wide default sink.</summary>
public static class AuditLog
{
    public static IAuditSink Default { get; } = new EventLogAuditSink();
}
