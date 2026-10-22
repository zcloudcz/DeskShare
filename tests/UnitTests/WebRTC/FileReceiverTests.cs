using System.Security.Cryptography;
using System.Text.Json;
using DeskShare.Core.Models;
using DeskShare.Core.WebRTC;
using DeskShare.Desktop.Shared.Services;
using NSubstitute;
using Serilog;

namespace DeskShare.UnitTests.WebRTC;

/// <summary>
/// Host-side file receiving. The peer controls name, size, chunk order and hash, so each safety rule
/// (folder containment, name sanitizing, no overwrite, verified before it becomes visible) has its own test.
/// </summary>
public sealed class FileReceiverTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deskshare-recv-" + Guid.NewGuid().ToString("N"));
    private readonly List<JsonElement> _replies = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private FileReceiver Create(long maxBytes = 1_000_000, TimeSpan? idle = null) =>
        new(_folder, maxBytes, json => { lock (_replies) _replies.Add(JsonDocument.Parse(json).RootElement.Clone()); return true; },
            Substitute.For<ILogger>(), idle);

    private static JsonElement Msg(object o) => JsonSerializer.SerializeToElement(o);

    private static string NewId() => Guid.NewGuid().ToString("D");

    private string[] Files() => Directory.Exists(_folder) ? Directory.GetFiles(_folder).Select(Path.GetFileName).OrderBy(x => x).ToArray()! : [];

    private string LastType() { lock (_replies) return _replies[^1].GetProperty("type").GetString()!; }
    private string LastReason() { lock (_replies) return _replies[^1].GetProperty("reason").GetString()!; }

    private static string Chunk(string id, long seq, byte[] data) =>
        JsonSerializer.Serialize(new { type = "file_chunk", id, seq, data = Convert.ToBase64String(data) });

    private void Send(FileReceiver r, string json)
    {
        using var doc = JsonDocument.Parse(json);
        r.Handle(doc.RootElement.GetProperty("type").GetString()!, doc.RootElement);
    }

    private void Offer(FileReceiver r, string id, string name, long size) =>
        Send(r, JsonSerializer.Serialize(new { type = "file_offer", id, name, size }));

    private void End(FileReceiver r, string id, byte[] all) =>
        Send(r, JsonSerializer.Serialize(new { type = "file_end", id, sha256 = Convert.ToHexString(SHA256.HashData(all)).ToLowerInvariant() }));

    // ---- name sanitizing ----

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("/etc/shadow", "shadow")]
    [InlineData("C:\\Windows\\win.ini", "win.ini")]
    [InlineData("C:evil.txt", "evil.txt")]
    [InlineData("\\\\server\\share\\x.txt", "x.txt")]
    [InlineData("//server/share/x.txt", "x.txt")]
    [InlineData("file.txt:hidden", "hidden")]            // alternate data stream syntax
    [InlineData("..", "file")]
    [InlineData(".", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("   ", "file")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "defgh.txt")]     // the colon cuts at the drive/stream part
    [InlineData("a<b>c|d?e*f.txt", "abcdef.txt")]
    [InlineData("name.txt. . ", "name.txt")]
    [InlineData("trailing  ", "trailing")]
    [InlineData("tab\tand\nnewline.txt", "tabandnewline.txt")]
    [InlineData("zprava \u010de\u0161tina \u2603.txt", "zprava \u010de\u0161tina \u2603.txt")]  // unicode is kept
    [InlineData("cod\u202Etxt.exe", "codtxt.exe")]       // right-to-left override is dropped
    public void Sanitize_ReturnsOnlyAPlainFileName(string? input, string expected)
    {
        Assert.Equal(expected, FileReceiver.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("PRN.tar.gz")]
    [InlineData("aux")]
    [InlineData("NUL.")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT1.txt")]
    [InlineData("CON .txt")]
    [InlineData("COM\u00B9.txt")]
    public void Sanitize_RenamesWindowsReservedNames(string input)
    {
        var result = FileReceiver.SanitizeFileName(input);

        Assert.StartsWith("_", result);
    }

    [Theory]
    [InlineData("console.txt")]
    [InlineData("com10.txt")]
    [InlineData("lpt0")]
    public void Sanitize_KeepsNamesThatOnlyLookReserved(string input)
    {
        Assert.Equal(input, FileReceiver.SanitizeFileName(input));
    }

    [Fact]
    public void Sanitize_LimitsLengthButKeepsTheExtension()
    {
        var result = FileReceiver.SanitizeFileName(new string('a', 500) + ".pdf");

        Assert.Equal(FileReceiver.MaxNameLength, result.Length);
        Assert.EndsWith(".pdf", result);
    }

    [Fact]
    public void Sanitize_DoesNotSplitASurrogatePair()
    {
        // Emoji are two chars each; a cut in the middle would leave a lone surrogate (invalid file name).
        var result = FileReceiver.SanitizeFileName(new string('a', 199) + "\U0001F600\U0001F600");

        for (var i = 0; i < result.Length; i++)
        {
            if (char.IsHighSurrogate(result[i]))
            {
                Assert.True(i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]));
            }
        }
    }

    // ---- containment and numbering ----

    [Theory]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/../../x")]
    public void ResolveInside_Throws_WhenThePathLeavesTheFolder(string name)
    {
        if (name.Contains('\\') && !OperatingSystem.IsWindows())
        {
            return; // a backslash is an ordinary character on Linux/macOS
        }

        Assert.Throws<InvalidOperationException>(() => FileReceiver.ResolveInside(_folder, name));
    }

    [Fact]
    public void ResolveInside_RejectsASiblingFolderWithTheSamePrefix()
    {
        // "<folder>-evil" starts with the folder path text, so a plain prefix check without a separator would accept it.
        Assert.Throws<InvalidOperationException>(() => FileReceiver.ResolveInside(_folder, "../" + Path.GetFileName(_folder) + "-evil/x"));
    }

    [Fact]
    public void ResolveInside_AcceptsAPlainName()
    {
        var path = FileReceiver.ResolveInside(_folder, "a.txt");

        Assert.Equal(Path.Combine(Path.GetFullPath(_folder), "a.txt"), path);
    }

    [Fact]
    public void UniqueName_NumbersBeforeTheExtension()
    {
        var taken = new HashSet<string> { "a.txt", "a (1).txt", "a (2).txt" };

        Assert.Equal("a (3).txt", FileReceiver.UniqueName("a.txt", taken.Contains));
        Assert.Equal("b.txt", FileReceiver.UniqueName("b.txt", taken.Contains));
        Assert.Equal("noext (1)", FileReceiver.UniqueName("noext", n => n == "noext"));
    }

    // ---- protocol ----

    [Fact]
    public void Transfer_SavesTheExactBytes_AndAnswersDone()
    {
        using var r = Create();
        var data = RandomNumberGenerator.GetBytes(100_000);
        var id = NewId();
        ReceivedFile? received = null;
        r.FileReceived += (_, f) => received = f;

        Offer(r, id, "data.bin", data.Length);
        Assert.Equal("file_accept", LastType());
        Send(r, Chunk(id, 0, data[..45_000]));
        Send(r, Chunk(id, 1, data[45_000..90_000]));
        Send(r, Chunk(id, 2, data[90_000..]));
        End(r, id, data);

        Assert.Equal("file_done", LastType());
        Assert.Equal(["data.bin"], Files());
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_folder, "data.bin")));
        Assert.Equal("data.bin", received!.SavedAs);
        Assert.Equal(data.Length, received.SizeBytes);
    }

    [Fact]
    public void EmptyFile_IsAccepted()
    {
        using var r = Create();
        var id = NewId();

        Offer(r, id, "empty.txt", 0);
        End(r, id, []);

        Assert.Equal("file_done", LastType());
        Assert.Equal(["empty.txt"], Files());
    }

    [Fact]
    public void TraversalName_LandsSanitizedInsideTheFolder()
    {
        using var r = Create();
        var id = NewId();
        var data = new byte[] { 1, 2, 3 };

        Offer(r, id, "../../etc/passwd", data.Length);
        Send(r, Chunk(id, 0, data));
        End(r, id, data);

        Assert.Equal(["passwd"], Files());
        Assert.False(File.Exists(Path.Combine(_folder, "..", "passwd")));
    }

    [Fact]
    public void ExistingFile_IsNeverOverwritten()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "a.txt"), "original");
        using var r = Create();

        for (var i = 0; i < 2; i++)
        {
            var id = NewId();
            var data = new byte[] { (byte)i };
            Offer(r, id, "a.txt", 1);
            Send(r, Chunk(id, 0, data));
            End(r, id, data);
        }

        Assert.Equal(["a (1).txt", "a (2).txt", "a.txt"], Files());
        Assert.Equal("original", File.ReadAllText(Path.Combine(_folder, "a.txt")));
    }

    [Fact]
    public void Offer_IsRejected_WhenOverTheSizeLimit_AndNothingIsWritten()
    {
        using var r = Create(maxBytes: 1000);

        Offer(r, NewId(), "big.bin", 1001);

        Assert.Equal("file_reject", LastType());
        Assert.Equal("too_big", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void SecondOffer_IsRejected_WhileOneTransferRuns()
    {
        using var r = Create();
        Offer(r, NewId(), "one.bin", 10);

        Offer(r, NewId(), "two.bin", 10);

        Assert.Equal("file_reject", LastType());
        Assert.Equal("busy", LastReason());
        Assert.Equal(["one.bin.part"], Files());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("../../x")]
    [InlineData("")]
    public void Offer_WithAnIdThatIsNoGuid_GetsNoReply(string id)
    {
        using var r = Create();

        Offer(r, id, "x.bin", 1);

        Assert.Empty(_replies);
        Assert.Empty(Files());
    }

    [Fact]
    public void Chunk_OutOfSequence_FailsAndDeletesThePartFile()
    {
        using var r = Create();
        var id = NewId();
        Offer(r, id, "x.bin", 100);

        Send(r, Chunk(id, 1, new byte[10])); // seq 0 is missing

        Assert.Equal("file_error", LastType());
        Assert.Equal("bad_sequence", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void Chunk_Repeated_Fails()
    {
        using var r = Create();
        var id = NewId();
        Offer(r, id, "x.bin", 100);
        Send(r, Chunk(id, 0, new byte[10]));

        Send(r, Chunk(id, 0, new byte[10]));

        Assert.Equal("bad_sequence", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void Chunk_MoreThanTheOfferedSize_Fails()
    {
        using var r = Create();
        var id = NewId();
        Offer(r, id, "x.bin", 5);

        Send(r, Chunk(id, 0, new byte[6]));

        Assert.Equal("too_much_data", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void Chunk_LargerThanTheChunkLimit_Fails()
    {
        using var r = Create();
        var id = NewId();
        Offer(r, id, "x.bin", 100_000);

        Send(r, Chunk(id, 0, new byte[FileReceiver.MaxChunkBytes + 1]));

        Assert.Equal("bad_chunk", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void Chunk_WithInvalidBase64_Fails()
    {
        using var r = Create();
        var id = NewId();
        Offer(r, id, "x.bin", 100);

        Send(r, JsonSerializer.Serialize(new { type = "file_chunk", id, seq = 0, data = "***not base64***" }));

        Assert.Equal("bad_chunk", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void Chunk_ForAnotherId_IsIgnored()
    {
        using var r = Create();
        var id = NewId();
        Offer(r, id, "x.bin", 10);

        Send(r, Chunk(NewId(), 0, new byte[10]));
        var data = new byte[10];
        Send(r, Chunk(id, 0, data));
        End(r, id, data);

        Assert.Equal("file_done", LastType());
    }

    [Fact]
    public void End_WithAWrongHash_DeletesThePartFile_AndSavesNothing()
    {
        using var r = Create();
        var id = NewId();
        var data = new byte[] { 1, 2, 3 };
        Offer(r, id, "x.bin", 3);
        Send(r, Chunk(id, 0, data));

        End(r, id, [9, 9, 9]);

        Assert.Equal("file_error", LastType());
        Assert.Equal("hash_mismatch", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void End_BeforeAllBytesArrived_Fails()
    {
        using var r = Create();
        var id = NewId();
        var data = new byte[] { 1, 2, 3 };
        Offer(r, id, "x.bin", 10);
        Send(r, Chunk(id, 0, data));

        End(r, id, data);

        Assert.Equal("size_mismatch", LastReason());
        Assert.Empty(Files());
    }

    [Fact]
    public void Cancel_DeletesThePartFile_AndAllowsANewTransfer()
    {
        using var r = Create();
        var id = NewId();
        Offer(r, id, "x.bin", 10);
        Send(r, Chunk(id, 0, new byte[5]));

        Send(r, JsonSerializer.Serialize(new { type = "file_cancel", id }));
        Assert.Empty(Files());

        Offer(r, NewId(), "y.bin", 1);
        Assert.Equal("file_accept", LastType());
    }

    [Fact]
    public void Abort_OnDisconnect_DeletesThePartFile()
    {
        using var r = Create();
        Offer(r, NewId(), "x.bin", 10);
        Assert.Equal(["x.bin.part"], Files());

        r.Abort();

        Assert.Empty(Files());
    }

    [Fact]
    public void Dispose_DeletesThePartFile()
    {
        var r = Create();
        Offer(r, NewId(), "x.bin", 10);

        r.Dispose();

        Assert.Empty(Files());
    }

    [Fact]
    public async Task IdleTransfer_TimesOut_AndTheViewerIsTold()
    {
        using var r = Create(idle: TimeSpan.FromMilliseconds(150));
        Offer(r, NewId(), "x.bin", 10);

        await WaitUntil(() => Files().Length == 0);

        Assert.Equal("file_error", LastType());
        Assert.Equal("timeout", LastReason());
    }

    [Fact]
    public async Task ActiveTransfer_DoesNotTimeOut_WhileChunksKeepArriving()
    {
        using var r = Create(idle: TimeSpan.FromMilliseconds(400));
        var id = NewId();
        Offer(r, id, "x.bin", 4);

        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(150);
            Send(r, Chunk(id, i, [1]));
        }

        End(r, id, [1, 1, 1, 1]);
        Assert.Equal("file_done", LastType());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
    }

    // ---- policy: the data channel must not reach the receiver without the grant ----

    private static DataChannelManager CreateManager(FileReceiver? receiver, bool granted)
    {
        var manager = new DataChannelManager(Substitute.For<ILogger>())
        {
            FileReceiver = receiver,
            HostGrantedCapabilities = granted ? SessionCapabilities.FileTransfer : SessionCapabilities.None
        };
        return manager;
    }

    [Theory]
    [InlineData("""{"type":"file_offer","id":"11111111-1111-1111-1111-111111111111","name":"a.txt","size":1}""")]
    [InlineData("""{"type":"file_chunk","id":"11111111-1111-1111-1111-111111111111","seq":0,"data":"AA=="}""")]
    [InlineData("""{"type":"file_end","id":"11111111-1111-1111-1111-111111111111","sha256":"00"}""")]
    [InlineData("""{"type":"file_cancel","id":"11111111-1111-1111-1111-111111111111"}""")]
    public void Policy_DeniesEveryFileMessage_WithoutTheGrant(string json)
    {
        using var receiver = Create();
        var manager = CreateManager(receiver, granted: false);

        manager.ProcessInputMessage(json);

        Assert.Empty(_replies);
        Assert.Empty(Files());
        Assert.False(Directory.Exists(_folder));
    }

    [Theory]
    [InlineData("file_accept")]
    [InlineData("file_reject")]
    [InlineData("file_done")]
    [InlineData("file_error")]
    public void Policy_DeniesHostToViewerTypes_EvenWithTheGrant(string type)
    {
        using var receiver = Create();
        var manager = CreateManager(receiver, granted: true);

        manager.ProcessInputMessage($$"""{"type":"{{type}}","id":"11111111-1111-1111-1111-111111111111"}""");

        Assert.Empty(_replies);
        Assert.Empty(Files());
    }

    [Fact]
    public void Policy_RoutesToTheReceiver_WithTheGrant()
    {
        using var receiver = Create();
        var manager = CreateManager(receiver, granted: true);

        manager.ProcessInputMessage("""{"type":"file_offer","id":"11111111-1111-1111-1111-111111111111","name":"a.txt","size":1}""");

        Assert.Equal("file_accept", LastType());
    }

    [Fact]
    public void Policy_WithGrantButNoReceiver_DoesNothing()
    {
        var manager = CreateManager(null, granted: true);

        manager.ProcessInputMessage("""{"type":"file_offer","id":"11111111-1111-1111-1111-111111111111","name":"a.txt","size":1}""");

        Assert.False(Directory.Exists(_folder));
    }

    // ---- the .NET viewer's uploader talking to the receiver ----

    [Fact]
    public async Task Uploader_SendsAFileThatArrivesIdentical()
    {
        FileUploader? uploader = null;
        // Wire both ends together in memory: what one sends, the other handles.
        var receiverSide = new FileReceiver(_folder, 10_000_000,
            json => { Deliver(json, m => uploader!.OnHostMessage(m)); return true; }, Substitute.For<ILogger>());
        uploader = new FileUploader(json => { Deliver(json, m => receiverSide.Handle(m.GetProperty("type").GetString()!, m)); return true; }, () => 0);

        var source = Path.Combine(Path.GetTempPath(), "deskshare-src-" + Guid.NewGuid().ToString("N") + ".bin");
        var data = RandomNumberGenerator.GetBytes(250_000); // several chunks plus a partial one
        await File.WriteAllBytesAsync(source, data);
        try
        {
            var savedAs = await uploader.SendAsync(source, null, CancellationToken.None);

            Assert.Equal(Path.GetFileName(source), savedAs);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_folder, savedAs)));
        }
        finally
        {
            File.Delete(source);
            receiverSide.Dispose();
        }
    }

    [Fact]
    public async Task Uploader_Throws_WhenTheHostRejects()
    {
        FileUploader? uploader = null;
        uploader = new FileUploader(json =>
        {
            Deliver(json, m => uploader!.OnHostMessage(JsonSerializer.SerializeToElement(
                new { type = "file_reject", id = m.GetProperty("id").GetString(), reason = "disabled" })));
            return true;
        }, () => 0);
        var source = Path.Combine(Path.GetTempPath(), "deskshare-src-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(source, [1]);
        try
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => uploader.SendAsync(source, null, CancellationToken.None));
            Assert.Contains("disabled", ex.Message);
        }
        finally
        {
            File.Delete(source);
        }
    }

    private static void Deliver(string json, Action<JsonElement> target)
    {
        using var doc = JsonDocument.Parse(json);
        target(doc.RootElement.Clone());
    }
}
