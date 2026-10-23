using DeskShare.SignalingServer.Services;

namespace DeskShare.IntegrationTests.Signaling;

/// <summary>Runs only against a real PostgreSQL: DESKSHARE_TEST_POSTGRES = Npgsql connection string.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKSHARE_TEST_POSTGRES")))
            Skip = "Set DESKSHARE_TEST_POSTGRES to a connection string (docker run -e POSTGRES_PASSWORD=test -p 55432:5432 postgres:17)";
    }
}

public class PostgresDeviceClaimStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // Unique ids per test, so parallel runs sharing one database never collide; each test deletes what it saved.
    private static string NewId() => "server-" + Guid.NewGuid().ToString("N");

    private static PostgresDeviceClaimStore NewStore() =>
        new(Environment.GetEnvironmentVariable("DESKSHARE_TEST_POSTGRES")!);

    [PostgresFact]
    public async Task Claim_RoundTrips_ListsPerAccount_AndDeletes()
    {
        var store = NewStore();
        var account = "acct-" + Guid.NewGuid().ToString("N");
        var serverId = NewId();
        var claim = new DeviceClaim(serverId, account, "Office PC", true, "fp", T0, T0);

        await store.SaveAsync(claim);
        Assert.Equal(claim, await store.GetAsync(serverId));
        Assert.Equal(claim, Assert.Single(await store.ListAsync(account)));
        Assert.Empty(await store.ListAsync("someone-else-" + account));

        await store.DeleteAsync(serverId);
        Assert.Null(await store.GetAsync(serverId));
        Assert.Empty(await store.ListAsync(account));
    }

    [PostgresFact]
    public async Task Save_ReplacesTheOwner()
    {
        var store = NewStore();
        var serverId = NewId();
        var claimB = new DeviceClaim(serverId, "acct-b", "B", false, "fp-b", T0, T0);
        await store.SaveAsync(new DeviceClaim(serverId, "acct-a", "A", true, "fp-a", T0, T0));
        await store.SaveAsync(claimB);

        Assert.Equal(claimB, await store.GetAsync(serverId));
        await store.DeleteAsync(serverId);
    }

    [PostgresFact]
    public async Task TouchLastSeen_UpdatesOnlyLastSeen_WhenOwnerMatches()
    {
        var store = NewStore();
        var claim = new DeviceClaim(NewId(), "acct-a", "A", false, "fp", T0, T0);
        await store.SaveAsync(claim);

        await store.TouchLastSeenAsync(claim, T0.AddHours(1));

        Assert.Equal(claim with { LastSeenUtc = T0.AddHours(1) }, await store.GetAsync(claim.ServerId));
        await store.DeleteAsync(claim.ServerId);
    }

    [PostgresFact]
    public async Task TouchLastSeen_DoesNotCreateARowAfterDelete()
    {
        var store = NewStore();
        var claim = new DeviceClaim(NewId(), "acct-a", "A", false, "fp", T0, T0);
        await store.SaveAsync(claim);
        await store.DeleteAsync(claim.ServerId);

        await store.TouchLastSeenAsync(claim, T0.AddHours(1));

        Assert.Null(await store.GetAsync(claim.ServerId));
    }

    [PostgresFact]
    public async Task TouchLastSeen_DoesNothing_AfterAnotherAccountReclaimed()
    {
        var store = NewStore();
        var seenByA = new DeviceClaim(NewId(), "acct-a", "A", false, "fp", T0, T0);
        var claimB = seenByA with { AccountKey = "acct-b", Name = "B" };
        await store.SaveAsync(seenByA);
        await store.SaveAsync(claimB);

        await store.TouchLastSeenAsync(seenByA, T0.AddHours(1));

        Assert.Equal(claimB, await store.GetAsync(seenByA.ServerId)); // owner and LastSeen untouched
        await store.DeleteAsync(seenByA.ServerId);
    }

    [PostgresFact]
    public async Task ReturnedDates_AreUtc()
    {
        var store = NewStore();
        var claim = new DeviceClaim(NewId(), "acct-a", "A", false, "fp", T0, T0);
        await store.SaveAsync(claim);

        var loaded = (await store.GetAsync(claim.ServerId))!;

        Assert.Equal(DateTimeKind.Utc, loaded.ClaimedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.LastSeenUtc.Kind);
        await store.DeleteAsync(claim.ServerId);
    }
}
