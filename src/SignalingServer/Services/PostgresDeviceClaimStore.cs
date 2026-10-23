using Npgsql;

namespace DeskShare.SignalingServer.Services;

/// <summary>
/// Table "device_claims" in PostgreSQL, created on first use. Only claims live here; sessions stay wherever
/// Storage:Type puts them. The primary key on server_id makes "one claim per Server ID" a database guarantee.
/// </summary>
public sealed class PostgresDeviceClaimStore : IDeviceClaimStore
{
    private const string Columns = "server_id, account_key, name, unattended, owner_fingerprint, claimed_at, last_seen";
    private readonly NpgsqlDataSource _db;

    /// <summary>Opens a pooled data source and creates the table and index if they are missing.</summary>
    public PostgresDeviceClaimStore(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        using var cmd = _db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS device_claims (
                server_id         text PRIMARY KEY,
                account_key       text NOT NULL,
                name              text NOT NULL,
                unattended        boolean NOT NULL,
                owner_fingerprint text NOT NULL,
                claimed_at        timestamptz NOT NULL,
                last_seen         timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_device_claims_account_key ON device_claims (account_key);
            """);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public async Task<DeviceClaim?> GetAsync(string serverId)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM device_claims WHERE server_id = @id");
        cmd.Parameters.AddWithValue("id", serverId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeviceClaim>> ListAsync(string accountKey)
    {
        var result = new List<DeviceClaim>();
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM device_claims WHERE account_key = @a");
        cmd.Parameters.AddWithValue("a", accountKey);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(Read(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(DeviceClaim claim)
    {
        // Upsert: one atomic statement, so two concurrent claims of a Server ID cannot both "insert".
        await using var cmd = _db.CreateCommand($"""
            INSERT INTO device_claims ({Columns}) VALUES (@id, @a, @n, @u, @f, @c, @l)
            ON CONFLICT (server_id) DO UPDATE SET account_key = EXCLUDED.account_key, name = EXCLUDED.name,
                unattended = EXCLUDED.unattended, owner_fingerprint = EXCLUDED.owner_fingerprint,
                claimed_at = EXCLUDED.claimed_at, last_seen = EXCLUDED.last_seen
            """);
        cmd.Parameters.AddWithValue("id", claim.ServerId);
        cmd.Parameters.AddWithValue("a", claim.AccountKey);
        cmd.Parameters.AddWithValue("n", claim.Name);
        cmd.Parameters.AddWithValue("u", claim.Unattended);
        cmd.Parameters.AddWithValue("f", claim.OwnerFingerprint);
        cmd.Parameters.AddWithValue("c", AsUtc(claim.ClaimedAtUtc));
        cmd.Parameters.AddWithValue("l", AsUtc(claim.LastSeenUtc));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string serverId)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM device_claims WHERE server_id = @id");
        cmd.Parameters.AddWithValue("id", serverId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <inheritdoc/>
    public async Task TouchLastSeenAsync(DeviceClaim seen, DateTime lastSeenUtc)
    {
        // Conditional UPDATE instead of read + write: the database checks "same account and owner" and writes in one
        // atomic step. A removed row matches nothing (an UPDATE never inserts) and a re-claimed one fails the owner check.
        await using var cmd = _db.CreateCommand("""
            UPDATE device_claims SET last_seen = @t
            WHERE server_id = @id AND account_key = @a AND owner_fingerprint = @f
            """);
        cmd.Parameters.AddWithValue("t", AsUtc(lastSeenUtc));
        cmd.Parameters.AddWithValue("id", seen.ServerId);
        cmd.Parameters.AddWithValue("a", seen.AccountKey);
        cmd.Parameters.AddWithValue("f", seen.OwnerFingerprint);
        await cmd.ExecuteNonQueryAsync();
    }

    // Npgsql refuses a non-UTC DateTime for timestamptz, so normalize (an Unspecified kind is taken as UTC).
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // Npgsql returns timestamptz as Kind=Utc already; SpecifyKind keeps that explicit for the record's equality.
    private static DeviceClaim Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4),
        DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc), DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc));
}
