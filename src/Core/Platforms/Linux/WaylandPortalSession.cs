using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Tmds.DBus.Protocol;

namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// One xdg-desktop-portal session shared by the Wayland screen capturer and input controller.
/// </summary>
/// <remarks>
/// Wayland has no global screen grab or input injection; a sandbox-friendly D-Bus service, the desktop portal,
/// asks the user once and then hands out a PipeWire video stream (ScreenCast) and an input channel (RemoteDesktop).
/// A RemoteDesktop session that also selects ScreenCast sources gives both with a single consent dialog, so the
/// capturer and the input controller share it (see <see cref="SharedSessionCache{T}"/>: reference counted, kept
/// briefly after opening, dropped when the portal closes it).
/// Portals without RemoteDesktop (xdg-desktop-portal-wlr on sway/Hyprland) get a ScreenCast-only session and
/// <see cref="CanInjectInput"/> stays false.
///
/// persist_mode=2 plus the stored restore token lets later sessions start without a dialog where the portal
/// backend supports it (GNOME, KDE, xdpw >= 0.7). The token is single-use; every start returns a new one.
///
/// D-Bus client: Tmds.DBus.Protocol, the same package Avalonia already ships in the desktop app (0.21.3: the release with the fix for CVE-2026-39959), so
/// it adds no new assembly to the release. It supports Unix fd passing, which OpenPipeWireRemote needs.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class WaylandPortalSession : IRemoteDesktopPortal, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<WaylandPortalSession>();

    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    internal const string RemoteDesktopInterface = "org.freedesktop.portal.RemoteDesktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";

    // The consent dialog waits for a human, so this is generous; a headless test backend answers at once.
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(2);

    // 15 s covers the sender's startup (size probe, signaling connect, real capturer) so that a portal without
    // restore tokens asks only once. ponytail: fixed time; a slower startup still sees a second dialog.
    private static readonly SharedSessionCache<WaylandPortalSession> Shared = new(TimeSpan.FromSeconds(15));

    private readonly Connection _connection;
    private string _sessionHandle = "";
    private int _tokenCounter;
    private IDisposable? _closedSubscription;
    private int _disposed;

    /// <summary>PipeWire node id of the (single) selected monitor stream.</summary>
    public uint NodeId { get; private set; }

    /// <summary>Logical stream size from the portal (0 when the backend does not report it).</summary>
    public int StreamWidth { get; private set; }

    /// <inheritdoc cref="StreamWidth"/>
    public int StreamHeight { get; private set; }

    /// <summary>True when this is a RemoteDesktop session with pointer and keyboard granted.</summary>
    public bool CanInjectInput { get; private set; }

    /// <summary>True once the portal closed the session (e.g. the user stopped sharing from the desktop).</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Where the restore token is kept, next to the Server ID and the other persisted sender files.</summary>
    internal static string DefaultRestoreTokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShare", "wayland-restore-token");

    private WaylandPortalSession(Connection connection) => _connection = connection;

    /// <summary>
    /// True when the current Linux session is Wayland and the X11 path (x11grab/XTest) would only see XWayland
    /// windows or nothing at all. An X11 session, or a Wayland compositor without session type but with a
    /// usable $DISPLAY, keeps the X11 path exactly as before.
    /// </summary>
    internal static bool IsWaylandSession(Func<string, string?> getEnv)
    {
        var sessionType = getEnv("XDG_SESSION_TYPE");
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.IsNullOrEmpty(getEnv("WAYLAND_DISPLAY")) && string.IsNullOrEmpty(getEnv("DISPLAY"));
    }

    /// <summary>
    /// Returns the shared session, opening it (and possibly showing the portal's consent dialog) on first use.
    /// Every successful call must be paired with <see cref="Release"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The portal is unavailable or the user declined.</exception>
    public static WaylandPortalSession Acquire() => Shared.Acquire(() =>
    {
        // Run on the thread pool: the D-Bus continuations must not need a caller's synchronization context.
        var session = Task.Run(OpenAsync).GetAwaiter().GetResult();
        session._onClosed = () => Shared.Invalidate(session);
        return session;
    });

    /// <summary>Drops one reference taken by <see cref="Acquire"/>.</summary>
    public static void Release(WaylandPortalSession session) => Shared.Release(session);

    /// <summary>Closes <paramref name="session"/> and forgets it, so the next <see cref="Acquire"/> opens a new one.</summary>
    public static void Invalidate(WaylandPortalSession session) => Shared.Invalidate(session);

    private Action? _onClosed;

    private static async Task<WaylandPortalSession> OpenAsync()
    {
        var address = Address.Session ?? throw new InvalidOperationException("No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS)");
        var connection = new Connection(address);
        var session = new WaylandPortalSession(connection);
        try
        {
            await connection.ConnectAsync();
            await session.StartAsync(DefaultRestoreTokenPath);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task StartAsync(string restoreTokenPath)
    {
        var screenCastVersion = await GetVersionAsync(ScreenCastInterface)
            ?? throw new InvalidOperationException("The desktop portal has no ScreenCast interface (is xdg-desktop-portal with a backend for this desktop installed?)");
        var remoteDesktopVersion = await GetVersionAsync(RemoteDesktopInterface);
        var restoreToken = ReadRestoreToken(restoreTokenPath);
        var useRemoteDesktop = remoteDesktopVersion != null;

        Logger.Information("[WaylandPortal] ScreenCast v{ScreenCast}, RemoteDesktop {RemoteDesktop}; restore token {Token}",
            screenCastVersion, remoteDesktopVersion?.ToString() ?? "unavailable", restoreToken != null ? "present" : "none");

        var created = await RequestAsync(useRemoteDesktop ? RemoteDesktopInterface : ScreenCastInterface, "CreateSession", null,
            new() { ["session_handle_token"] = NewToken() });
        _sessionHandle = created.TryGetValue("session_handle", out var handle)
            ? handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString()
            : throw new InvalidOperationException("Portal CreateSession returned no session_handle");

        // For a RemoteDesktop session persistence is requested on SelectDevices (SelectSources rejects it there).
        // RemoteDesktop persistence exists since interface version 2, ScreenCast since version 4.
        if (useRemoteDesktop)
        {
            var devices = new Dictionary<string, VariantValue> { ["types"] = (uint)(DeviceKeyboard | DevicePointer) };
            if (remoteDesktopVersion >= 2) AddPersistence(devices, restoreToken);
            await RequestAsync(RemoteDesktopInterface, "SelectDevices", _sessionHandle, devices);
        }

        var sources = new Dictionary<string, VariantValue> { ["types"] = SourceMonitor, ["multiple"] = false };
        // Embedded cursor (2) matches what x11grab delivers on X11; only ask when the backend offers it.
        if ((await GetUInt32PropertyAsync(ScreenCastInterface, "AvailableCursorModes") & CursorEmbedded) != 0)
            sources["cursor_mode"] = CursorEmbedded;
        if (!useRemoteDesktop && screenCastVersion >= 4) AddPersistence(sources, restoreToken);
        await RequestAsync(ScreenCastInterface, "SelectSources", _sessionHandle, sources);

        var started = await RequestAsync(useRemoteDesktop ? RemoteDesktopInterface : ScreenCastInterface, "Start", _sessionHandle,
            new(), parentWindow: true);

        if (!started.TryGetValue("streams", out var streams) || streams.Count == 0)
            throw new InvalidOperationException("Portal Start returned no streams");
        (NodeId, StreamWidth, StreamHeight) = ParseFirstStream(streams);

        var granted = started.TryGetValue("devices", out var d) ? d.GetUInt32() : 0;
        CanInjectInput = useRemoteDesktop && (granted & DevicePointer) != 0 && (granted & DeviceKeyboard) != 0;

        WriteRestoreToken(restoreTokenPath, started.TryGetValue("restore_token", out var token) ? token.GetString() : null);

        // The user can end the session from the desktop at any time; a closed session must not be handed out again.
        _closedSubscription = await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.portal.Session", Member = "Closed", Path = _sessionHandle, Sender = PortalService },
            (_, _) => true,
            (ex, _, _, _) =>
            {
                if (ex != null) return;
                Logger.Information("[WaylandPortal] The portal closed the session (sharing stopped from the desktop)");
                IsClosed = true;
                _onClosed?.Invoke();
            },
            ObserverFlags.None, null, null, emitOnCapturedContext: false);
        Logger.Information("[WaylandPortal] Session started: PipeWire node {Node}, {Width}x{Height}, input {Input}",
            NodeId, StreamWidth, StreamHeight, CanInjectInput ? "granted" : "not available");
    }

    // Portal enum values (org.freedesktop.portal.ScreenCast / RemoteDesktop documentation).
    internal const uint DeviceKeyboard = 1, DevicePointer = 2, SourceMonitor = 1, CursorEmbedded = 2, PersistUntilRevoked = 2;

    private static void AddPersistence(Dictionary<string, VariantValue> options, string? restoreToken)
    {
        options["persist_mode"] = PersistUntilRevoked;
        if (restoreToken != null) options["restore_token"] = restoreToken;
    }

    private static (uint NodeId, int Width, int Height) ParseFirstStream(VariantValue streams)
    {
        // a(ua{sv}): node id plus properties; "size" is the logical (ii) size used for absolute pointer motion.
        var first = streams.GetItem(0);
        var properties = first.GetItem(1).GetDictionary<string, VariantValue>();
        return properties.TryGetValue("size", out var size)
            ? (first.GetItem(0).GetUInt32(), size.GetItem(0).GetInt32(), size.GetItem(1).GetInt32())
            : (first.GetItem(0).GetUInt32(), 0, 0);
    }

    /// <summary>The fd of a PipeWire connection that can only see this session's streams. Caller owns it.</summary>
    public SafeHandle OpenPipeWireRemote()
    {
        var message = CreatePortalCall(ScreenCastInterface, "OpenPipeWireRemote", _sessionHandle, parentWindow: false, new());
        return Task.Run(() => _connection.CallMethodAsync(message, (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(), null))
            .GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("OpenPipeWireRemote returned no file descriptor");
    }

    /// <inheritdoc />
    public bool NotifyPointerMotionAbsolute(double x, double y)
    {
        using var writer = BeginNotify("NotifyPointerMotionAbsolute", "udd");
        writer.WriteUInt32(NodeId);
        writer.WriteDouble(x);
        writer.WriteDouble(y);
        return _connection.TrySendMessage(writer.CreateMessage());
    }

    /// <inheritdoc />
    public bool NotifyPointerButton(int button, bool pressed)
    {
        using var writer = BeginNotify("NotifyPointerButton", "iu");
        writer.WriteInt32(button);
        writer.WriteUInt32(pressed ? 1u : 0u);
        return _connection.TrySendMessage(writer.CreateMessage());
    }

    /// <inheritdoc />
    public bool NotifyPointerAxisDiscrete(int steps)
    {
        using var writer = BeginNotify("NotifyPointerAxisDiscrete", "ui");
        writer.WriteUInt32(0); // vertical axis
        writer.WriteInt32(steps);
        return _connection.TrySendMessage(writer.CreateMessage());
    }

    /// <inheritdoc />
    public bool NotifyKeyboardKeycode(int keycode, bool pressed)
    {
        using var writer = BeginNotify("NotifyKeyboardKeycode", "iu");
        writer.WriteInt32(keycode);
        writer.WriteUInt32(pressed ? 1u : 0u);
        return _connection.TrySendMessage(writer.CreateMessage());
    }

    /// <summary>
    /// Starts a fire-and-forget RemoteDesktop Notify* call: the header plus the session and empty options that
    /// every Notify method begins with. No reply is requested so input never waits for a D-Bus round trip.
    /// </summary>
    private MessageWriter BeginNotify(string member, string argumentSignature)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(PortalService, PortalPath, RemoteDesktopInterface, member,
            "oa{sv}" + argumentSignature, MessageFlags.NoReplyExpected);
        writer.WriteObjectPath(_sessionHandle);
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        return writer;
    }

    /// <summary>
    /// Calls a portal method that answers through a Request object and waits for its Response signal.
    /// The signal is subscribed before the call (on the path the portal will use for our handle_token),
    /// so a fast backend cannot answer before we listen.
    /// </summary>
    private async Task<Dictionary<string, VariantValue>> RequestAsync(string @interface, string method, string? session,
        Dictionary<string, VariantValue> options, bool parentWindow = false)
    {
        var token = NewToken();
        options["handle_token"] = token;
        var requestPath = RequestPath(_connection.UniqueName ?? "", token);

        var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.portal.Request", Member = "Response", Path = requestPath, Sender = PortalService },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
            },
            (ex, value, _, _) =>
            {
                if (ex != null) response.TrySetException(ex);
                else response.TrySetResult(value);
            },
            ObserverFlags.None, null, null, emitOnCapturedContext: false);

        await _connection.CallMethodAsync(CreatePortalCall(@interface, method, session, parentWindow, options));

        var (code, results) = await response.Task.WaitAsync(ResponseTimeout);
        return code switch
        {
            0 => results,
            1 => throw new InvalidOperationException($"Screen sharing was declined in the portal dialog ({method})"),
            _ => throw new InvalidOperationException($"Portal {method} failed (response {code})"),
        };
    }

    /// <summary>
    /// Builds a portal method call: [session] [parent window] options. Kept out of the async methods because
    /// MessageWriter is a ref struct, which C# 12 does not allow there.
    /// </summary>
    private MessageBuffer CreatePortalCall(string @interface, string method, string? session, bool parentWindow,
        Dictionary<string, VariantValue> options)
    {
        using var writer = _connection.GetMessageWriter();
        var signature = (session != null ? "o" : "") + (parentWindow ? "s" : "") + "a{sv}";
        writer.WriteMethodCallHeader(PortalService, PortalPath, @interface, method, signature);
        if (session != null) writer.WriteObjectPath(session);
        if (parentWindow) writer.WriteString(""); // no parent window: the dialog is not attached to one
        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    /// <summary>
    /// The Request object path the portal creates for a handle_token: the caller's unique bus name without
    /// the leading ':' and with '.' replaced by '_' (org.freedesktop.portal.Request documentation).
    /// </summary>
    internal static string RequestPath(string uniqueName, string token) =>
        $"{PortalPath}/request/{uniqueName.TrimStart(':').Replace('.', '_')}/{token}";

    private string NewToken() => $"deskshare{Environment.ProcessId}_{Interlocked.Increment(ref _tokenCounter)}";

    /// <summary>Interface version, or null when the portal does not implement the interface.</summary>
    private async Task<uint?> GetVersionAsync(string @interface)
    {
        try
        {
            return await GetUInt32PropertyAsync(@interface, "version");
        }
        catch (DBusException ex)
        {
            Logger.Debug("[WaylandPortal] {Interface} not available: {Error}", @interface, ex.ErrorMessage);
            return null;
        }
    }

    private Task<uint> GetUInt32PropertyAsync(string @interface, string property)
    {
        MessageBuffer message;
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(PortalService, PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss");
            writer.WriteString(@interface);
            writer.WriteString(property);
            message = writer.CreateMessage();
        }
        return _connection.CallMethodAsync(message, (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt32(), null);
    }

    /// <summary>
    /// The stored token, or null when there is none or it cannot be trusted: the token lets a later start skip
    /// the consent dialog, so a file someone else could have written (symlink, foreign owner, readable or
    /// writable by group/others) is ignored.
    /// </summary>
    internal static string? ReadRestoreToken(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;
            if (!OperatingSystem.IsWindows())
            {
                if (file.LinkTarget != null || !EnsurePrivateDirectory(file.DirectoryName!) || (file.UnixFileMode & GroupOrOther) != 0
                    || !IsOwnedByUs(path, file.UnixFileMode))
                {
                    Logger.Warning("[WaylandPortal] Ignoring restore token {Path}: not a private file of this user", path);
                    return null;
                }
            }
            var token = File.ReadAllText(path).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores the token the portal returned. The old one is consumed by every start, so no new token means
    /// the file is removed rather than keeping a token that would no longer work.
    /// </summary>
    /// <remarks>
    /// Written to a fresh temp file created exclusively (O_CREAT|O_EXCL, which also refuses a pre-planted
    /// symlink) with mode 0600 from the start, then renamed over the token: rename replaces a symlink at the
    /// path instead of following it, and readers never see a half-written file.
    /// </remarks>
    internal static void WriteRestoreToken(string path, string? token)
    {
        var directory = Path.GetDirectoryName(path)!;
        string? temp = null;
        try
        {
            if (!EnsurePrivateDirectory(directory))
            {
                Logger.Warning("[WaylandPortal] Not storing the restore token: {Dir} is not a private directory of this user", directory);
                return;
            }
            if (string.IsNullOrEmpty(token))
            {
                File.Delete(path); // removes a symlink itself, never its target
                return;
            }

            temp = Path.Combine(directory, $".wayland-restore-token.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(token);
            File.Move(temp, path, overwrite: true);
            temp = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "[WaylandPortal] Could not store the restore token; the next start will ask again");
        }
        finally
        {
            if (temp != null)
                try { File.Delete(temp); } catch (IOException) { /* best effort */ }
        }
    }

    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Creates the directory as 0700, or tightens an existing one to 0700; false when it is not ours.</summary>
    private static bool EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (new DirectoryInfo(directory).LinkTarget != null)
            return false;
        // chmod only succeeds for the owner (or root), so this both enforces 0700 and proves the directory is ours.
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// .NET has no API for a file's owner; re-applying the current mode is a chmod, which only the owner (or root)
    /// may do, so it proves ownership without changing anything.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static bool IsOwnedByUs(string path, UnixFileMode currentMode)
    {
        try
        {
            File.SetUnixFileMode(path, currentMode);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _closedSubscription?.Dispose();
        var closedByPortal = IsClosed;
        IsClosed = true; // holders (the input controller) then reacquire instead of using a dead connection

        // Closing the D-Bus connection ends the portal session too, but an explicit Close removes the
        // "screen is being shared" indicator immediately.
        if (_sessionHandle.Length > 0 && !closedByPortal)
        {
            try
            {
                MessageBuffer message;
                {
                    using var writer = _connection.GetMessageWriter();
                    writer.WriteMethodCallHeader(PortalService, _sessionHandle, "org.freedesktop.portal.Session", "Close");
                    message = writer.CreateMessage();
                }
                // Wait briefly for the reply so the call is flushed before the connection is torn down.
                Task.Run(() => _connection.CallMethodAsync(message)).Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "[WaylandPortal] Session close failed");
            }
        }
        _connection.Dispose();
    }
}
