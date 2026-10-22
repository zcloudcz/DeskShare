using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// Two-way text clipboard sync over the data channel, shared by the host and the .NET viewer.
/// Nothing is sent or applied while <see cref="Enabled"/> is false (the host's grant / the viewer's opt-in).
/// Wire format in both directions: <c>{"type":"clipboard","data":{ClipboardMessage}}</c>.
/// Clipboard content is never logged here; callers log sizes only.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    /// <summary>Largest text in UTF-8 bytes. Leaves room for the JSON envelope under the 64 KB message limit.</summary>
    public const int MaxTextBytes = 60_000;

    // Relaxed escaping keeps non-ASCII text at its UTF-8 size; the default encoder would write every
    // non-ASCII char as a 6-byte \uXXXX and blow up the message size.
    private static readonly JsonSerializerOptions WireOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IClipboardManager _clipboard;
    private readonly Func<string, bool> _send;
    private long _sequence;
    private volatile string? _lastRemoteText;
    private volatile bool _enabled;

    /// <param name="clipboard">Local clipboard to watch and write to.</param>
    /// <param name="send">Sends one JSON message to the peer; returns false when it could not be sent.</param>
    public ClipboardSync(IClipboardManager clipboard, Func<string, bool> send)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _clipboard.ClipboardChanged += OnLocalChanged;
    }

    /// <summary>Master switch. False: local changes are not sent and remote messages are not applied.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Raised with a user-visible reason when text was refused (for example too large).</summary>
    public event EventHandler<string>? Rejected;

    /// <summary>Raised after remote text was written to the local clipboard.</summary>
    public event EventHandler<ClipboardMessage>? Applied;

    /// <summary>Builds the wire JSON for a text, or null (with a reason) when it is over the size limit.</summary>
    public static string? BuildMessage(string text, long sequence, out string? error)
    {
        error = null;
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxTextBytes)
        {
            error = $"Clipboard text is too large ({bytes} bytes, max {MaxTextBytes}); not shared.";
            return null;
        }

        var json = JsonSerializer.Serialize(new
        {
            type = "clipboard",
            data = new ClipboardMessage
            {
                ContentType = ClipboardContentType.Text,
                TextContent = text,
                SizeBytes = bytes,
                SequenceNumber = sequence
            }
        }, WireOptions);

        // Escaped quotes/newlines can still push a text under the byte limit over the message limit.
        if (Encoding.UTF8.GetByteCount(json) > DataChannelPolicy.MaxMessageBytes)
        {
            error = "Clipboard text is too large once encoded; not shared.";
            return null;
        }

        return json;
    }

    /// <summary>Sends a text to the peer. False when disabled, empty, too large or the channel is not open.</summary>
    public bool TrySendText(string? text)
    {
        if (!_enabled || string.IsNullOrEmpty(text))
        {
            return false;
        }

        var json = BuildMessage(text, Interlocked.Increment(ref _sequence), out var error);
        if (json == null)
        {
            Rejected?.Invoke(this, error!);
            return false;
        }

        return _send(json);
    }

    /// <summary>Writes a received text message to the local clipboard. False when it was not applied.</summary>
    public async Task<bool> ApplyAsync(ClipboardMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_enabled || message.ContentType != ClipboardContentType.Text || string.IsNullOrEmpty(message.TextContent))
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(message.TextContent) > MaxTextBytes)
        {
            Rejected?.Invoke(this, "Received clipboard text is too large; ignored.");
            return false;
        }

        // Remember it before writing: the clipboard change event for our own write must not be echoed back.
        _lastRemoteText = message.TextContent;
        if (!await _clipboard.SetTextAsync(message.TextContent))
        {
            return false;
        }

        Applied?.Invoke(this, message);
        return true;
    }

    private void OnLocalChanged(object? sender, ClipboardChangedEventArgs e)
    {
        if (e.Text == _lastRemoteText)
        {
            return;
        }

        TrySendText(e.Text);
    }

    public void Dispose() => _clipboard.ClipboardChanged -= OnLocalChanged;
}
