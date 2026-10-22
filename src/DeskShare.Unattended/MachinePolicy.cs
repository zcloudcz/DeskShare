using System.Text.Json;

namespace DeskShare.Unattended;

/// <summary>
/// Machine-wide settings an administrator deployed with the MSI (%ProgramData%\DeskShare.Policy\policy.json).
/// A value that is present WINS over the user's own setting and the app shows it as locked; a missing value leaves the
/// user free to choose. Shape mirrors the headless sender's config keys:
/// <c>{ "Signaling": { "ServerUrl": "wss://..." }, "Security": { "EnableRemoteControl": false, ... }, "Updates": { "Disabled": true } }</c>
/// The folder is read-only for standard users (see AclSpec.PolicyDirSddl); the reader does not verify that, because a user
/// who can edit it is already an admin on their own machine. ponytail: no ACL check on read, add one if policy ever has to
/// hold something a local user must not influence.
/// </summary>
public sealed class MachinePolicy
{
    public string? SignalingUrl { get; private init; }
    public bool? RemoteControl { get; private init; }
    public bool? Clipboard { get; private init; }
    public bool? FileReceive { get; private init; }
    public bool? PrivacyScreen { get; private init; }
    public bool UpdatesDisabled { get; private init; }

    /// <summary>Set when the file existed but could not be parsed (the policy is then empty); callers log it.</summary>
    public string? Warning { get; private init; }

    public static readonly MachinePolicy Empty = new();

    /// <summary>Folder is NOT inside %ProgramData%\DeskShare: that one is SYSTEM/Admins-only, so the user's app could not read it.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeskShare.Policy", "policy.json");

    private static readonly Lazy<MachinePolicy> CurrentLazy = new(() => Load(DefaultPath));

    /// <summary>The policy of this machine, read once per process.</summary>
    public static MachinePolicy Current => CurrentLazy.Value;

    /// <summary>
    /// Reads the policy only if <paramref name="trust"/> (default <see cref="PolicyTrust.Problem"/>) finds nothing wrong with
    /// the folder and file: a policy a standard user could have planted must never steer the SYSTEM agent. Untrusted = ignored + Warning.
    /// </summary>
    public static MachinePolicy Load(string path, Func<string, string?>? trust = null)
    {
        try
        {
            if (!File.Exists(path)) return Empty;
            var problem = (trust ?? (p => OperatingSystem.IsWindows() ? PolicyTrust.Problem(p) : null))(path);
            return problem == null ? Parse(File.ReadAllText(path)) : new MachinePolicy { Warning = $"{path} ignored: {problem}" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MachinePolicy { Warning = $"Could not read {path}: {ex.Message}" };
        }
    }

    /// <summary>Malformed JSON gives an empty policy plus a <see cref="Warning"/>; single wrong-typed values are skipped.</summary>
    public static MachinePolicy Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new MachinePolicy { Warning = "policy.json is not a JSON object, ignored." };
            return new MachinePolicy
            {
                SignalingUrl = WebSocketUrl(Child(root, "Signaling", "ServerUrl")),
                RemoteControl = Bool(Child(root, "Security", "EnableRemoteControl")),
                Clipboard = Bool(Child(root, "Security", "EnableClipboard")),
                FileReceive = Bool(Child(root, "Security", "EnableFileReceive")),
                PrivacyScreen = Bool(Child(root, "Security", "EnablePrivacyScreen")),
                UpdatesDisabled = Bool(Child(root, "Updates", "Disabled")) == true,
            };
        }
        catch (JsonException ex)
        {
            return new MachinePolicy { Warning = "policy.json is malformed and was ignored: " + ex.Message };
        }
    }

    /// <summary>Policy values as configuration keys, to be layered LAST so they beat appsettings / unattended.json / command line.</summary>
    public IEnumerable<KeyValuePair<string, string?>> ToConfiguration()
    {
        if (SignalingUrl != null)
        {
            yield return new("Signaling:ServerUrl", SignalingUrl); // headless sender
            yield return new("Server:SignalingServerUrl", SignalingUrl); // desktop app (host)
            yield return new("Client:SignalingServerUrl", SignalingUrl); // desktop app (viewer)
        }
        if (RemoteControl is { } rc) yield return new("Security:EnableRemoteControl", rc.ToString());
        if (Clipboard is { } cb) yield return new("Security:EnableClipboard", cb.ToString());
        if (FileReceive is { } fr) yield return new("Security:EnableFileReceive", fr.ToString());
        if (PrivacyScreen is { } ps) yield return new("Security:EnablePrivacyScreen", ps.ToString());
    }

    /// <summary>Serialises only what is set (used by the MSI custom action; null = left to the user).</summary>
    public static string ToJson(string? signalingUrl, bool? remoteControl, bool? clipboard, bool? fileReceive, bool? privacyScreen, bool updatesDisabled)
    {
        var security = new Dictionary<string, bool>();
        if (remoteControl is { } rc) security["EnableRemoteControl"] = rc;
        if (clipboard is { } cb) security["EnableClipboard"] = cb;
        if (fileReceive is { } fr) security["EnableFileReceive"] = fr;
        if (privacyScreen is { } ps) security["EnablePrivacyScreen"] = ps;
        var root = new Dictionary<string, object>();
        if (signalingUrl != null) root["Signaling"] = new Dictionary<string, string> { ["ServerUrl"] = signalingUrl };
        if (security.Count > 0) root["Security"] = security;
        root["Updates"] = new Dictionary<string, bool> { ["Disabled"] = updatesDisabled };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>True when the exe runs from the MSI's per-machine folder %ProgramFiles%\DeskShare\App (Velopack is not in play there).</summary>
    public static bool IsMsiInstall(string baseDir)
    {
        var msiDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeskShare", "App");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)).Equals(msiDir, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement? Child(JsonElement root, string section, string key) =>
        root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(key, out var v) ? v : null;

    private static bool? Bool(JsonElement? e) => e is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;

    /// <summary>Only absolute ws/wss URLs: a typo must not point every client of the machine at a bogus scheme.</summary>
    private static string? WebSocketUrl(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.String } v && IsWebSocketUrl(v.GetString()) ? v.GetString() : null;

    public static bool IsWebSocketUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "ws" or "wss";
}
