using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using DeskShare.Core.WebRTC;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Viewer side of the file transfer: offers a file to the host, streams it in chunks and waits for the
/// host's verdict (see <see cref="FileReceiver"/> for the protocol). One file at a time.
/// </summary>
public sealed class FileUploader
{
    // Stop queuing chunks while this much is waiting in the data channel, otherwise a 2 GB file would sit in memory.
    private const ulong MaxBufferedBytes = 1024 * 1024;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string, bool> _send;
    private readonly Func<ulong> _bufferedAmount;
    private volatile Channel<JsonElement>? _replies;
    private volatile string? _currentId;

    /// <param name="send">Sends one JSON message to the host; false when the channel is not open.</param>
    /// <param name="bufferedAmount">Bytes queued in the data channel and not yet sent.</param>
    public FileUploader(Func<string, bool> send, Func<ulong> bufferedAmount)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _bufferedAmount = bufferedAmount ?? throw new ArgumentNullException(nameof(bufferedAmount));
    }

    /// <summary>Feeds a message from the host (file_accept, file_reject, file_done, file_error, file_cancel).</summary>
    public void OnHostMessage(JsonElement root)
    {
        var channel = _replies;
        if (channel != null && root.TryGetProperty("id", out var id) && id.GetString() == _currentId)
        {
            channel.Writer.TryWrite(root.Clone());
        }
    }

    /// <summary>
    /// Sends the file and returns the name it was saved under on the host. <paramref name="offeredName"/> replaces the
    /// file name in the offer (the host sanitizes whatever it gets; tests use it to send a hostile name).
    /// Throws <see cref="IOException"/> with the host's reason when the host refuses or fails the transfer.
    /// </summary>
    public async Task<string> SendAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken, string? offeredName = null)
    {
        if (_replies != null)
        {
            throw new InvalidOperationException("A file transfer is already running.");
        }

        var info = new FileInfo(path);
        var id = Guid.NewGuid().ToString("D");
        var replies = Channel.CreateUnbounded<JsonElement>();
        _currentId = id;
        _replies = replies;

        try
        {
            Send(new { type = "file_offer", id, name = offeredName ?? info.Name, size = info.Length });
            var accept = await NextReplyAsync(replies, cancellationToken);
            if (accept.GetProperty("type").GetString() != "file_accept")
            {
                throw Refused(accept);
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var buffer = new byte[FileReceiver.MaxChunkBytes];
            long seq = 0, sent = 0;

            int read;
            while ((read = await ReadFullAsync(stream, buffer, cancellationToken)) > 0)
            {
                while (_bufferedAmount() > MaxBufferedBytes)
                {
                    ThrowIfHostStopped(replies);
                    await Task.Delay(10, cancellationToken);
                }

                ThrowIfHostStopped(replies);
                hash.AppendData(buffer, 0, read);
                // Built by hand: the envelope is fixed and this runs once per 45 KB.
                var json = $"{{\"type\":\"file_chunk\",\"id\":\"{id}\",\"seq\":{seq++},\"data\":\"{Convert.ToBase64String(buffer, 0, read)}\"}}";
                if (!_send(json))
                {
                    throw new IOException("The connection to the host is closed.");
                }

                sent += read;
                progress?.Report(info.Length == 0 ? 1 : (double)sent / info.Length);
            }

            Send(new { type = "file_end", id, sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() });
            var done = await NextReplyAsync(replies, cancellationToken);
            if (done.GetProperty("type").GetString() != "file_done")
            {
                throw Refused(done);
            }

            return done.TryGetProperty("savedAs", out var savedAs) ? savedAs.GetString() ?? info.Name : info.Name;
        }
        catch (OperationCanceledException)
        {
            _send(JsonSerializer.Serialize(new { type = "file_cancel", id })); // free the host's .part file
            throw;
        }
        finally
        {
            _replies = null;
            _currentId = null;
        }
    }

    private void Send(object message)
    {
        if (!_send(JsonSerializer.Serialize(message)))
        {
            throw new IOException("The connection to the host is closed.");
        }
    }

    private static async Task<JsonElement> NextReplyAsync(Channel<JsonElement> replies, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReplyTimeout);
        try
        {
            return await replies.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The host did not answer in time.");
        }
    }

    // A host that failed or cancelled the transfer says so asynchronously; stop sending instead of filling the channel.
    private static void ThrowIfHostStopped(Channel<JsonElement> replies)
    {
        if (replies.Reader.TryRead(out var message))
        {
            throw Refused(message);
        }
    }

    private static IOException Refused(JsonElement message)
    {
        var reason = message.TryGetProperty("reason", out var r) ? r.GetString() : message.GetProperty("type").GetString();
        return new IOException($"The host refused the file: {reason}");
    }

    // FileStream.ReadAsync may return fewer bytes than asked; keep chunks full so the sequence is predictable.
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
