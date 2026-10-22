using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace DeskShare.Core.WebRTC;

/// <summary>A file that was received completely and verified.</summary>
public sealed record ReceivedFile(string SavedAs, string FullPath, long SizeBytes);

/// <summary>
/// Receives one file at a time from the viewer over the data channel (offer, chunks, end).
/// Everything the peer sends (name, size, order, hash) is untrusted; the rules that keep the host safe live here:
/// the file lands only inside <c>folder</c>, under a sanitized name, never over an existing file, and only
/// becomes visible (rename from <c>.part</c>) after size and SHA-256 match.
/// File contents are never logged.
/// </summary>
public sealed class FileReceiver : IDisposable
{
    /// <summary>Raw bytes per chunk. Base64 grows it by 4/3 (60,000 chars), which with the JSON envelope stays under the 64 KB message limit.</summary>
    public const int MaxChunkBytes = 45_000;

    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxNameLength = 200;
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly string _folder;
    private readonly long _maxBytes;
    private readonly Func<string, bool> _send;
    private readonly ILogger _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly object _gate = new();
    private readonly Timer _idleTimer;
    private readonly TimeProvider _time;

    private Transfer? _active;
    // Guarded by _gate. A timer callback can be queued before a chunk/new offer takes the lock, so the callback
    // re-checks this instead of trusting that it fired for the current transfer.
    private long _lastActivity;
    private bool _disposed;

    /// <summary>State of the single transfer in flight.</summary>
    private sealed class Transfer
    {
        public required string Id;
        public required string SafeName;
        public required long Size;
        public required string PartPath;
        public required FileStream Stream;
        public readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long NextSeq;
        public long Received;
    }

    /// <param name="folder">Destination folder; created when missing.</param>
    /// <param name="maxBytes">Largest accepted file.</param>
    /// <param name="send">Sends one JSON reply to the viewer.</param>
    public FileReceiver(string folder, long maxBytes, Func<string, bool> send, ILogger logger, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
        _maxBytes = maxBytes;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _idleTimer = new Timer(_ => OnIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The folder files are saved into (full path).</summary>
    public string Folder => _folder;

    /// <summary>Raised after a file was verified and saved.</summary>
    public event EventHandler<ReceivedFile>? FileReceived;

    /// <summary>
    /// Handles one parsed file_* message. The capability check already happened in the data channel policy.
    /// Never throws for bad peer input: it answers with file_reject / file_error instead.
    /// </summary>
    public void Handle(string type, JsonElement root)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                switch (type)
                {
                    case "file_offer": HandleOffer(root); break;
                    case "file_chunk": HandleChunk(root); break;
                    case "file_end": HandleEnd(root); break;
                    case "file_cancel": HandleCancel(root); break;
                }
            }
            catch (Exception ex)
            {
                // Disk full, permissions, ... : report a generic reason, keep the details in the host log only.
                _logger.Error(ex, "File receive failed");
                var id = _active?.Id ?? TryGetId(root);
                Fail(id, "io_error");
            }
        }
    }

    /// <summary>Drops the transfer in flight and deletes its .part file (viewer disconnected).</summary>
    public void Abort()
    {
        lock (_gate)
        {
            if (_active != null)
            {
                _logger.Information("File transfer aborted: {Name}", _active.SafeName);
                Discard();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Discard();
            _disposed = true;
        }

        _idleTimer.Dispose();
    }

    private void HandleOffer(JsonElement root)
    {
        var id = TryGetId(root);
        if (id == null)
        {
            return; // without a valid id there is nobody to answer
        }

        if (_active != null)
        {
            Reject(id, "busy"); // at most one transfer per viewer
            return;
        }

        if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("size", out var sizeEl) || !sizeEl.TryGetInt64(out var size) || size < 0)
        {
            Reject(id, "bad_offer");
            return;
        }

        var safeName = SanitizeFileName(nameEl.GetString());
        if (size > _maxBytes)
        {
            _logger.Warning("File offer rejected (too big): {Name}, {Size} bytes", safeName, size);
            Reject(id, "too_big");
            return;
        }

        Directory.CreateDirectory(_folder);
        var (stream, partPath) = CreatePartFile(_folder, safeName);

        _active = new Transfer { Id = id, SafeName = safeName, Size = size, PartPath = partPath, Stream = stream };
        TouchActivity();
        _logger.Information("Receiving file: {Name}, {Size} bytes", safeName, size);
        _send(JsonSerializer.Serialize(new { type = "file_accept", id }));
    }

    private void HandleChunk(JsonElement root)
    {
        var t = _active;
        if (t == null || TryGetId(root) != t.Id)
        {
            return; // stale chunk of a finished/rejected transfer
        }

        if (!root.TryGetProperty("seq", out var seqEl) || !seqEl.TryGetInt64(out var seq) || seq != t.NextSeq)
        {
            Fail(t.Id, "bad_sequence");
            return;
        }

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
        {
            Fail(t.Id, "bad_chunk");
            return;
        }

        var base64 = dataEl.GetString()!;
        // Decode into a bounded buffer: a lying peer must not make us allocate beyond one chunk.
        var buffer = new byte[MaxChunkBytes];
        if (base64.Length > MaxChunkBytes / 3 * 4 + 4 || !Convert.TryFromBase64String(base64, buffer, out var written))
        {
            Fail(t.Id, "bad_chunk");
            return;
        }

        if (t.Received + written > t.Size)
        {
            Fail(t.Id, "too_much_data");
            return;
        }

        t.Stream.Write(buffer, 0, written);
        t.Hash.AppendData(buffer, 0, written);
        t.Received += written;
        t.NextSeq++;
        TouchActivity();
    }

    private void HandleEnd(JsonElement root)
    {
        var t = _active;
        if (t == null || TryGetId(root) != t.Id)
        {
            return;
        }

        if (t.Received != t.Size)
        {
            Fail(t.Id, "size_mismatch");
            return;
        }

        var expected = root.TryGetProperty("sha256", out var shaEl) && shaEl.ValueKind == JsonValueKind.String ? shaEl.GetString() : null;
        var actual = Convert.ToHexString(t.Hash.GetHashAndReset());
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            Fail(t.Id, "hash_mismatch");
            return;
        }

        t.Stream.Dispose();
        t.Hash.Dispose();
        var finalPath = MoveToFinalName(t.PartPath, _folder, t.SafeName);
        var savedAs = Path.GetFileName(finalPath);
        var id = t.Id;
        var size = t.Size;

        _active = null;
        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.Information("File received: {Name}, {Size} bytes", savedAs, size);
        _send(JsonSerializer.Serialize(new { type = "file_done", id, savedAs }));
        FileReceived?.Invoke(this, new ReceivedFile(savedAs, finalPath, size));
    }

    private void HandleCancel(JsonElement root)
    {
        var t = _active;
        if (t != null && TryGetId(root) == t.Id)
        {
            _logger.Information("File transfer cancelled by viewer: {Name}", t.SafeName);
            Discard();
        }
    }

    private void TouchActivity()
    {
        _lastActivity = _time.GetTimestamp();
        _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
    }

    internal void OnIdle()
    {
        lock (_gate)
        {
            if (_active == null || _disposed)
            {
                return;
            }

            // Stale callback (a chunk or a newer transfer refreshed the activity while it waited for the lock): re-arm for the rest.
            var idle = _time.GetElapsedTime(_lastActivity);
            if (idle < _idleTimeout)
            {
                _idleTimer.Change(_idleTimeout - idle, Timeout.InfiniteTimeSpan);
                return;
            }

            _logger.Warning("File transfer timed out: {Name}", _active.SafeName);
            Fail(_active.Id, "timeout");
        }
    }

    private void Reject(string id, string reason) =>
        _send(JsonSerializer.Serialize(new { type = "file_reject", id, reason }));

    /// <summary>Reports the error to the viewer and removes the partial file.</summary>
    private void Fail(string? id, string reason)
    {
        if (_active != null)
        {
            _logger.Warning("File transfer failed: {Name}, reason {Reason}", _active.SafeName, reason);
        }

        Discard();
        if (id != null)
        {
            _send(JsonSerializer.Serialize(new { type = "file_error", id, reason }));
        }
    }

    private void Discard()
    {
        var t = _active;
        _active = null;
        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (t == null)
        {
            return;
        }

        t.Stream.Dispose();
        t.Hash.Dispose();
        try { File.Delete(t.PartPath); }
        catch (Exception ex) { _logger.Warning(ex, "Could not delete partial file {Name}", t.SafeName); }
    }

    /// <summary>The id must be a GUID; it is echoed back to the peer, so anything else is dropped.</summary>
    internal static string? TryGetId(JsonElement root) =>
        root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
        && Guid.TryParse(idEl.GetString(), out var guid)
            ? guid.ToString("D")
            : null;

    #region Names and paths

    // Fixed set (not Path.GetInvalidFileNameChars) so the result is the same on every OS: a file saved on
    // Linux must also be a valid name when the folder is later copied to Windows.
    private static readonly char[] InvalidNameChars = "<>:\"/\\|?*".ToCharArray();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
    };

    /// <summary>
    /// Turns whatever the peer sent into a plain file name: only the last path segment (any drive, UNC,
    /// <c>..</c> or separators before it are dropped), no invalid or control characters, no trailing dots or
    /// spaces, no Windows reserved device names, at most <see cref="MaxNameLength"/> chars. Never empty.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "file";
        }

        // Last segment after any separator or drive colon ("C:\a\b.txt", "//srv/share/b.txt", "../../x").
        var cut = name.LastIndexOfAny(['/', '\\', ':']);
        var segment = cut >= 0 ? name[(cut + 1)..] : name;

        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            // Format chars (e.g. right-to-left override) would let "cod\u202Etxt.exe" look like another extension.
            if (char.IsControl(ch) || Array.IndexOf(InvalidNameChars, ch) >= 0
                || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
            {
                continue;
            }

            sb.Append(ch);
        }

        var clean = sb.ToString().Trim(' ').TrimEnd('.', ' ');
        if (clean.Length == 0)
        {
            return "file";
        }

        // Windows treats "CON", "con.txt" and "CON .txt" all as the device.
        var stemEnd = clean.IndexOf('.');
        var stem = (stemEnd >= 0 ? clean[..stemEnd] : clean).TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            clean = "_" + clean;
        }

        if (clean.Length > MaxNameLength)
        {
            var ext = Path.GetExtension(clean);
            if (ext.Length > 20)
            {
                ext = "";
            }

            var keep = MaxNameLength - ext.Length;
            if (char.IsHighSurrogate(clean[keep - 1]))
            {
                keep--; // do not cut a surrogate pair in half
            }

            clean = (clean[..keep].TrimEnd('.', ' ') + ext);
        }

        return clean;
    }

    /// <summary>
    /// Full path of <paramref name="name"/> inside <paramref name="folder"/>. Throws when the result would
    /// land anywhere else (defence in depth on top of <see cref="SanitizeFileName"/>).
    /// </summary>
    public static string ResolveInside(string folder, string name)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("File path escapes the receive folder.");
        }

        return full;
    }

    /// <summary>"name.ext", then "name (1).ext", "name (2).ext", ... - the first one for which <paramref name="taken"/> is false.</summary>
    public static string UniqueName(string name, Func<string, bool> taken)
    {
        if (!taken(name))
        {
            return name;
        }

        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }

    // A name is taken when the final file or someone else's .part for it exists.
    private static bool IsTaken(string folder, string name) =>
        File.Exists(ResolveInside(folder, name)) || File.Exists(ResolveInside(folder, name + ".part"));

    /// <summary>Opens "&lt;unique name&gt;.part" exclusively (CreateNew), retrying if another writer wins the race.</summary>
    private static (FileStream Stream, string PartPath) CreatePartFile(string folder, string safeName)
    {
        while (true)
        {
            var name = UniqueName(safeName, n => IsTaken(folder, n));
            var partPath = ResolveInside(folder, name + ".part");
            try
            {
                return (new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), partPath);
            }
            catch (IOException) when (File.Exists(partPath))
            {
                // lost the race for this name: pick the next free one
            }
        }
    }

    /// <summary>Renames the verified .part to its final name without overwriting anything.</summary>
    private static string MoveToFinalName(string partPath, string folder, string safeName)
    {
        while (true)
        {
            // The .part itself exists, so only the final name matters here.
            var name = UniqueName(safeName, n => File.Exists(ResolveInside(folder, n)));
            var finalPath = ResolveInside(folder, name);
            try
            {
                File.Move(partPath, finalPath, overwrite: false);
                return finalPath;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // created in the meantime: try the next number
            }
        }
    }

    #endregion
}
