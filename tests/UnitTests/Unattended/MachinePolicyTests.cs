using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using DeskShare.Unattended;

namespace DeskShare.UnitTests.Unattended;

public class MachinePolicyTests
{
    private const string Full = """
        { "Signaling": { "ServerUrl": "wss://signal.example.com/signal" },
          "Security": { "EnableRemoteControl": true, "EnableClipboard": false, "EnableFileReceive": false, "EnablePrivacyScreen": true },
          "Updates": { "Disabled": true } }
        """;

    [Fact]
    public void Parse_ReadsAllKeys()
    {
        var p = MachinePolicy.Parse(Full);
        Assert.Equal("wss://signal.example.com/signal", p.SignalingUrl);
        Assert.True(p.RemoteControl);
        Assert.False(p.Clipboard);
        Assert.False(p.FileReceive);
        Assert.True(p.PrivacyScreen);
        Assert.True(p.UpdatesDisabled);
        Assert.Null(p.Warning);
    }

    [Fact]
    public void Parse_MissingKeysStayUnset_SoUserKeepsControl()
    {
        var p = MachinePolicy.Parse("""{ "Security": { "EnableClipboard": true } }""");
        Assert.True(p.Clipboard);
        Assert.Null(p.RemoteControl);
        Assert.Null(p.FileReceive);
        Assert.Null(p.SignalingUrl);
        Assert.False(p.UpdatesDisabled);
    }

    [Fact]
    public void PolicyValueWinsOverUserSetting()
    {
        var p = MachinePolicy.Parse(Full);
        bool userWantsClipboard = true, userWantsRemote = false;
        Assert.False(p.Clipboard ?? userWantsClipboard); // locked off by the admin
        Assert.True(p.RemoteControl ?? userWantsRemote); // locked on by the admin
        Assert.True(MachinePolicy.Empty.Clipboard ?? userWantsClipboard); // no policy -> user value
    }

    [Fact]
    public void ToConfiguration_OverridesConfigurationLayersBelowIt()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Signaling:ServerUrl"] = "wss://user/", ["Security:EnableRemoteControl"] = "false" })
            .AddInMemoryCollection(MachinePolicy.Parse(Full).ToConfiguration())
            .Build();
        Assert.Equal("wss://signal.example.com/signal", config["Signaling:ServerUrl"]);
        Assert.Equal("wss://signal.example.com/signal", config["Client:SignalingServerUrl"]);
        Assert.True(config.GetValue<bool>("Security:EnableRemoteControl"));
    }

    [Fact]
    public void Load_MissingFile_IsEmptyWithoutWarning()
    {
        var p = MachinePolicy.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.Null(p.RemoteControl);
        Assert.Null(p.Warning);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void Parse_MalformedFile_IsIgnoredWithWarning(string json)
    {
        var p = MachinePolicy.Parse(json);
        Assert.NotNull(p.Warning);
        Assert.Null(p.RemoteControl);
        Assert.Null(p.SignalingUrl);
        Assert.False(p.UpdatesDisabled);
    }

    [Fact]
    public void Load_MalformedFile_IsIgnoredWithWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ broken");
        try { Assert.NotNull(MachinePolicy.Load(path).Warning); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_UntrustedFile_IsIgnoredWithWarning_TrustedFileIsRead()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, Full);
        try
        {
            var untrusted = MachinePolicy.Load(path, _ => "Users has write access");
            Assert.Null(untrusted.RemoteControl);
            Assert.Contains("Users has write access", untrusted.Warning);
            Assert.True(MachinePolicy.Load(path, _ => null).RemoteControl);
        }
        finally { File.Delete(path); }
    }

    private static FileSystemAccessRule Allow(WellKnownSidType sid, FileSystemRights rights) =>
        new(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow);

    private static SecurityIdentifier Sid(WellKnownSidType sid) => new(sid, null);

    [Fact]
    public void AclProblem_AcceptsAdminOnlyWriteWithUsersReadOnly()
    {
        var rules = new[]
        {
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
        };
        Assert.Null(PolicyTrust.AclProblem(Sid(WellKnownSidType.BuiltinAdministratorsSid), true, true, rules));
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Write)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid, FileSystemRights.Modify)]
    [InlineData(WellKnownSidType.WorldSid, FileSystemRights.AppendData)]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.FullControl)]
    public void AclProblem_RejectsWriteForNonAdmins(WellKnownSidType who, FileSystemRights rights)
    {
        var rules = new[] { Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl), Allow(who, rights) };
        Assert.NotNull(PolicyTrust.AclProblem(Sid(WellKnownSidType.BuiltinAdministratorsSid), true, true, rules));
    }

    [Fact]
    public void AclProblem_RejectsUserOwnerAndUnprotectedFolder()
    {
        var ok = new[] { Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl) };
        Assert.NotNull(PolicyTrust.AclProblem(Sid(WellKnownSidType.BuiltinUsersSid), true, true, ok));  // owned by a user
        Assert.NotNull(PolicyTrust.AclProblem(null, true, true, ok));
        Assert.NotNull(PolicyTrust.AclProblem(Sid(WellKnownSidType.LocalSystemSid), true, false, ok));  // inherits from the parent
        Assert.Null(PolicyTrust.AclProblem(Sid(WellKnownSidType.LocalSystemSid), false, false, ok));    // a file inherits by design
    }

    [Fact]
    public void Parse_WrongTypesAndNonWebSocketUrl_AreSkippedIndividually()
    {
        var p = MachinePolicy.Parse("""
            { "Signaling": { "ServerUrl": "http://not-websocket" },
              "Security": { "EnableRemoteControl": "yes", "EnableClipboard": true } }
            """);
        Assert.Null(p.SignalingUrl);
        Assert.Null(p.RemoteControl);
        Assert.True(p.Clipboard);
    }

    [Fact]
    public void ToJson_RoundTripsAndOmitsUnsetValues()
    {
        var json = MachinePolicy.ToJson("wss://a/b", false, null, true, null, updatesDisabled: true);
        var p = MachinePolicy.Parse(json);
        Assert.Equal("wss://a/b", p.SignalingUrl);
        Assert.False(p.RemoteControl);
        Assert.Null(p.Clipboard);
        Assert.True(p.FileReceive);
        Assert.Null(p.PrivacyScreen);
        Assert.True(p.UpdatesDisabled);
    }

    [Fact]
    public void IsMsiInstall_OnlyForTheProgramFilesAppFolder()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(MachinePolicy.IsMsiInstall(Path.Combine(pf, "DeskShare", "App") + Path.DirectorySeparatorChar));
        Assert.False(MachinePolicy.IsMsiInstall(Path.Combine(pf, "DeskShare", "Service")));
        Assert.False(MachinePolicy.IsMsiInstall(Path.GetTempPath()));
    }
}
