using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;

namespace DeskShare.SignalingServer.Services;

/// <summary>A Server ID bound to a ZCLOUD account ("My devices"). One account per Server ID.</summary>
/// <param name="ServerId">The claimed Server ID.</param>
/// <param name="AccountKey">The account's "oid" (or "sub") claim, see AccountEndpoints.AccountKey.</param>
/// <param name="Name">Display name chosen by the user (defaults to the Server ID).</param>
/// <param name="Unattended">True for the unattended agent's Server ID (shown in the list).</param>
/// <param name="ClaimedAtUtc">When the claim was made.</param>
/// <param name="LastSeenUtc">Last time the device was seen online by a list call (or the claim time).</param>
/// <param name="OwnerFingerprint">Identifies the installation (owner secret) that proved the claim, without being
/// usable as a claim key; see AccountEndpoints.Fingerprint.</param>
public sealed record DeviceClaim(
    string ServerId,
    string AccountKey,
    string Name,
    bool Unattended,
    string OwnerFingerprint,
    DateTime ClaimedAtUtc,
    DateTime LastSeenUtc);

/// <summary>
/// Storage of device claims. Same split as the session storage: in memory by default, Azure Table when
/// Storage:Type = AzureTableStorage, so claims survive a restart there.
/// </summary>
public interface IDeviceClaimStore
{
    /// <summary>The claim of <paramref name="serverId"/>, or null when nobody claimed it.</summary>
    Task<DeviceClaim?> GetAsync(string serverId);

    /// <summary>All devices claimed by one account.</summary>
    Task<IReadOnlyList<DeviceClaim>> ListAsync(string accountKey);

    /// <summary>Creates or replaces the claim of its Server ID (the caller checked who may do that).</summary>
    Task SaveAsync(DeviceClaim claim);

    /// <summary>Removes the claim. Callers pass only a Server ID they checked belongs to the account.</summary>
    Task DeleteAsync(string serverId);

    /// <summary>
    /// Records "seen online" for a claim the caller read earlier. Changes LastSeen only, and only while the stored
    /// claim still has the same account and owner fingerprint as <paramref name="seen"/>: it never creates a claim or
    /// changes its owner. (A plain save of the old record could undo a removal and a new owner's claim that happened
    /// while the list request was waiting for the device status.) Losing the race just skips the update.
    /// </summary>
    Task TouchLastSeenAsync(DeviceClaim seen, DateTime lastSeenUtc);
}

/// <summary>Default backend: lost on restart, like the in-memory sessions.</summary>
public sealed class InMemoryDeviceClaimStore : IDeviceClaimStore
{
    private readonly ConcurrentDictionary<string, DeviceClaim> _claims = new();

    /// <inheritdoc/>
    public Task<DeviceClaim?> GetAsync(string serverId) =>
        Task.FromResult(_claims.TryGetValue(serverId, out var c) ? c : null);

    /// <inheritdoc/>
    public Task<IReadOnlyList<DeviceClaim>> ListAsync(string accountKey) =>
        Task.FromResult<IReadOnlyList<DeviceClaim>>(_claims.Values.Where(c => c.AccountKey == accountKey).ToList());

    /// <inheritdoc/>
    public Task SaveAsync(DeviceClaim claim)
    {
        _claims[claim.ServerId] = claim;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string serverId)
    {
        _claims.TryRemove(serverId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task TouchLastSeenAsync(DeviceClaim seen, DateTime lastSeenUtc)
    {
        // Compare-and-swap: replaces the entry only if it still equals what the caller read (records compare by
        // value), so a claim that was removed or re-claimed meanwhile is left alone and nothing is re-created.
        if (_claims.TryGetValue(seen.ServerId, out var current) &&
            current.AccountKey == seen.AccountKey && current.OwnerFingerprint == seen.OwnerFingerprint)
            _claims.TryUpdate(seen.ServerId, current with { LastSeenUtc = lastSeenUtc }, current);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Table "DeviceClaims": PartitionKey "claims", RowKey = ServerId, so a Server ID can have one claim only (the
/// storage enforces it) and the lookup during a claim is a point read.
/// </summary>
/// <remarks>
/// ponytail: listing filters the single partition by AccountKey (a partition scan). Fine for thousands of claims;
/// add a second row per account (PartitionKey = account) if it ever gets slow.
/// </remarks>
public sealed class AzureTableDeviceClaimStore : IDeviceClaimStore
{
    private const string Partition = "claims";
    private readonly TableClient _table;

    /// <summary>Opens (and creates if needed) the DeviceClaims table.</summary>
    public AzureTableDeviceClaimStore(string connectionString)
    {
        _table = new TableServiceClient(connectionString).GetTableClient("DeviceClaims");
        _table.CreateIfNotExists();
    }

    /// <inheritdoc/>
    public async Task<DeviceClaim?> GetAsync(string serverId)
    {
        var response = await _table.GetEntityIfExistsAsync<DeviceClaimEntity>(Partition, serverId);
        return response.HasValue ? response.Value!.ToClaim() : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeviceClaim>> ListAsync(string accountKey)
    {
        var result = new List<DeviceClaim>();
        // CreateQueryFilter escapes the value, so an account key can never change the filter.
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition} and AccountKey eq {accountKey}");
        await foreach (var entity in _table.QueryAsync<DeviceClaimEntity>(filter))
            result.Add(entity.ToClaim());
        return result;
    }

    /// <inheritdoc/>
    public Task SaveAsync(DeviceClaim claim) =>
        _table.UpsertEntityAsync(DeviceClaimEntity.From(claim), TableUpdateMode.Replace);

    /// <inheritdoc/>
    public Task DeleteAsync(string serverId) => _table.DeleteEntityAsync(Partition, serverId);

    /// <inheritdoc/>
    public async Task TouchLastSeenAsync(DeviceClaim seen, DateTime lastSeenUtc)
    {
        var response = await _table.GetEntityIfExistsAsync<DeviceClaimEntity>(Partition, seen.ServerId);
        if (!response.HasValue) return; // removed meanwhile: do not re-create it
        var current = response.Value!;
        if (current.AccountKey != seen.AccountKey || current.OwnerFingerprint != seen.OwnerFingerprint) return;

        // Merge of LastSeenUtc only, conditional on the ETag just read: if anything changed the row since, the
        // service answers 412 and we skip (this is only a "last seen" hint). A missing row answers 404, never an insert.
        var patch = new TableEntity(Partition, seen.ServerId) { [nameof(DeviceClaimEntity.LastSeenUtc)] = lastSeenUtc };
        try { await _table.UpdateEntityAsync(patch, current.ETag, TableUpdateMode.Merge); }
        catch (RequestFailedException ex) when (ex.Status is 412 or 404) { }
    }

    private sealed class DeviceClaimEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = Partition;
        public string RowKey { get; set; } = string.Empty; // ServerId
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public string AccountKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Unattended { get; set; }
        public string OwnerFingerprint { get; set; } = string.Empty;
        public DateTime ClaimedAtUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }

        public DeviceClaim ToClaim() => new(RowKey, AccountKey, Name, Unattended, OwnerFingerprint,
            DateTime.SpecifyKind(ClaimedAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(LastSeenUtc, DateTimeKind.Utc));

        public static DeviceClaimEntity From(DeviceClaim c) => new()
        {
            RowKey = c.ServerId, AccountKey = c.AccountKey, Name = c.Name, Unattended = c.Unattended,
            OwnerFingerprint = c.OwnerFingerprint, ClaimedAtUtc = c.ClaimedAtUtc, LastSeenUtc = c.LastSeenUtc
        };
    }
}
