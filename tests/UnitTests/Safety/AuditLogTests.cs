using DeskShare.Core.Safety;

namespace DeskShare.UnitTests.Safety;

/// <summary>Audit records: metadata only, no content; and the file-log fallback when the Event Log source is missing.</summary>
public sealed class AuditLogTests
{
    [Fact]
    public void SessionStarted_NamesAddressAndVerification()
    {
        var e = AuditEvents.SessionStarted("ab12cd34", "203.0.113.7", endToEndVerified: true);

        Assert.Equal(AuditKind.SessionStarted, e.Kind);
        Assert.Equal(100, e.EventId);
        Assert.Contains("ab12cd34", e.Text);
        Assert.Contains("203.0.113.7", e.Text);
        Assert.Contains("End-to-end verified: yes", e.Text);
        Assert.Contains("unknown", AuditEvents.SessionStarted("x", null, true).Text); // address not known from ICE
    }

    [Fact]
    public void Clipboard_RecordsDirectionAndSizeOnly()
    {
        var toHost = AuditEvents.ClipboardTransfer(toHost: true, 42);
        var fromHost = AuditEvents.ClipboardTransfer(toHost: false, 7);

        Assert.Contains("from the viewer to this computer", toHost.Text);
        Assert.Contains("(42 bytes)", toHost.Text);
        Assert.Contains("from this computer to the viewer", fromHost.Text);
        // The factory has no parameter for text at all: there is nothing that could leak a password.
        Assert.Equal(typeof(long), typeof(AuditEvents).GetMethod(nameof(AuditEvents.ClipboardTransfer))!.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void FileName_FromTheViewer_CannotInjectLines()
    {
        var e = AuditEvents.FileReceived("evil.txt\r\nSession 1 started by admin", 12);

        Assert.DoesNotContain('\r', e.Text);
        Assert.DoesNotContain('\n', e.Text);
        Assert.Contains("(12 bytes)", e.Text);
    }

    [Fact]
    public void LongNames_AreCut()
    {
        var e = AuditEvents.FileReceived(new string('a', 5000), 1);

        Assert.True(e.Text.Length < 300);
    }

    [Theory]
    [InlineData(true, AuditKind.PrivacyScreenOn)]
    [InlineData(false, AuditKind.PrivacyScreenOff)]
    public void PrivacyScreen_HasOneKindPerState(bool on, AuditKind expected) =>
        Assert.Equal(expected, AuditEvents.PrivacyScreen(on, null).Kind);

    [Fact]
    public void ControlAndDisplayEvents_HaveTheirOwnIds()
    {
        Assert.Equal(AuditKind.ControlGranted, AuditEvents.ControlGranted().Kind);
        Assert.Equal(AuditKind.ControlDenied, AuditEvents.ControlDenied("timeout").Kind);
        Assert.Equal(AuditKind.ControlRevoked, AuditEvents.ControlRevoked().Kind);
        Assert.Equal(AuditKind.DisplaySwitched, AuditEvents.DisplaySwitched("Display 2").Kind);
        Assert.Equal(AuditKind.SessionEnded, AuditEvents.SessionEnded("x").Kind);
    }

    [Fact]
    public void SourceExists_WritesToEventLog_NotToTheFallback()
    {
        var eventLog = new List<AuditEvent>();
        var fallback = new List<AuditEvent>();
        var sink = new EventLogAuditSink(() => true, eventLog.Add, fallback.Add);

        sink.Write(AuditEvents.ControlGranted());

        Assert.Single(eventLog);
        Assert.Empty(fallback);
    }

    [Fact]
    public void SourceMissing_UsesTheFileLogFallback_AndChecksOnlyOnce()
    {
        var checks = 0;
        var eventLog = new List<AuditEvent>();
        var fallback = new List<AuditEvent>();
        var sink = new EventLogAuditSink(() => { checks++; return false; }, eventLog.Add, fallback.Add);

        sink.Write(AuditEvents.ControlGranted());
        sink.Write(AuditEvents.ControlRevoked());

        Assert.Empty(eventLog);
        Assert.Equal(2, fallback.Count);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void SourceCheckThrows_FallsBackInsteadOfThrowing()
    {
        var fallback = new List<AuditEvent>();
        var sink = new EventLogAuditSink(() => throw new System.Security.SecurityException("limited user"), _ => { }, fallback.Add);

        sink.Write(AuditEvents.ControlGranted());

        Assert.Single(fallback);
    }

    [Fact]
    public void EventLogWriteFails_FallsBack_AndStaysOnTheFallback()
    {
        var fallback = new List<AuditEvent>();
        var sink = new EventLogAuditSink(() => true, _ => throw new InvalidOperationException("log full"), fallback.Add);

        sink.Write(AuditEvents.ControlGranted());
        sink.Write(AuditEvents.ControlRevoked());

        Assert.Equal(2, fallback.Count);
    }

    [Fact]
    public void EvenWhenTheFallbackFails_WriteNeverThrows()
    {
        var sink = new EventLogAuditSink(() => false, _ => { }, _ => throw new IOException("disk full"));

        sink.Write(AuditEvents.ControlGranted()); // must not throw into a running session
    }
}
