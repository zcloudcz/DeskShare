using DeskShare.Core.Safety;

namespace DeskShare.UnitTests.Safety;

/// <summary>Unattended agent: when does a new viewer have to be confirmed by the person at the computer?</summary>
public sealed class AdmissionPolicyTests
{
    [Theory]
    [InlineData(true, "Default", true)]   // user logged on, unlocked: ask
    [InlineData(true, "default", true)]   // desktop names are case-insensitive
    [InlineData(true, null, true)]        // unknown: ask rather than let somebody in unseen
    [InlineData(true, "Winlogon", false)] // lock screen / UAC / nobody logged on: nobody can answer
    [InlineData(false, "Default", false)] // setting off
    [InlineData(false, "Winlogon", false)]
    public void ShouldAsk_FollowsSettingAndDesktop(bool setting, string? desktop, bool expected) =>
        Assert.Equal(expected, AdmissionPolicy.ShouldAsk(setting, desktop));

    [Fact]
    public async Task LockedScreen_AdmitsWithoutShowingAnything()
    {
        var shown = false;
        var admit = AdmissionPolicy.Create(true, () => "Winlogon", _ => { shown = true; return Task.FromResult(false); });

        Assert.True(await admit(CancellationToken.None));
        Assert.False(shown);
    }

    [Fact]
    public async Task UnlockedScreen_AsksAndFollowsTheAnswer()
    {
        var allow = AdmissionPolicy.Create(true, () => "Default", _ => Task.FromResult(true));
        var deny = AdmissionPolicy.Create(true, () => "Default", _ => Task.FromResult(false));

        Assert.True(await allow(CancellationToken.None));
        Assert.False(await deny(CancellationToken.None));
    }

    [Fact]
    public async Task SettingOff_AdmitsEvenOnTheUsersDesktop()
    {
        var shown = false;
        var admit = AdmissionPolicy.Create(false, () => "Default", _ => { shown = true; return Task.FromResult(false); });

        Assert.True(await admit(CancellationToken.None));
        Assert.False(shown);
    }
}
