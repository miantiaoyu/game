using Npgsql;

namespace DarkTide.Persistence;

/// <summary>PostgreSQL 上的玩家凭据。换新用行锁（FOR UPDATE）串行化：同一张旧凭据并发换新时，后到的一方看到已替换。</summary>
public sealed class PostgresPlayerCredentialStore(NpgsqlDataSource dataSource) : IPlayerCredentialStore
{
    public async Task CreateGuestAsync(Guid playerId, byte[] credentialHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var player = new NpgsqlCommand("INSERT INTO players (player_id, kind) VALUES ($1, 'guest')", connection, transaction))
        {
            player.Parameters.AddWithValue(playerId);
            await player.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertCredentialAsync(connection, transaction, credentialHash, playerId, expiresAt, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RotationResult> RotateAsync(byte[] oldHash, IReadOnlyList<byte[]> successorHashes, DateTimeOffset now, DateTimeOffset successorExpiresAt,
        TimeSpan grace, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(successorHashes.Count);
        byte[] successorHash = successorHashes[0];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid playerId;
        CredentialRules.Snapshot old;
        await using (var select = new NpgsqlCommand(
            """
            SELECT c.player_id, c.expires_at, c.replaced_at, c.replaced_by, c.revoked_at, p.disabled_at
            FROM player_credentials c JOIN players p ON p.player_id = c.player_id
            WHERE c.credential_hash = $1
            FOR UPDATE OF c
            """, connection, transaction))
        {
            select.Parameters.AddWithValue(oldHash);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return RotationResult.Invalid;
            }
            playerId = reader.GetGuid(0);
            old = new CredentialRules.Snapshot(
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
        }

        bool successorUsed = false;
        DateTimeOffset successorExpiry = successorExpiresAt;
        if (old.ReplacedBy is not null)
        {
            await using var successor = new NpgsqlCommand(
                "SELECT expires_at, replaced_at IS NOT NULL OR revoked_at IS NOT NULL FROM player_credentials WHERE credential_hash = $1 FOR UPDATE",
                connection, transaction);
            successor.Parameters.AddWithValue(old.ReplacedBy);
            await using var reader = await successor.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            // 后继已被清理（过期删除）也算“用过”：不能凭一张很旧的凭据重新拿到身份。
            successorUsed = true;
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                successorExpiry = reader.GetFieldValue<DateTimeOffset>(0);
                successorUsed = reader.GetBoolean(1);
            }
        }

        var status = CredentialRules.Decide(old, successorUsed, successorHashes, now, grace, out int matched);
        switch (status)
        {
            case RotationStatus.Invalid:
                return RotationResult.Invalid;
            case RotationStatus.AlreadyRotated:
                return new RotationResult(status, playerId, successorExpiry, matched);
            case RotationStatus.ReuseDetected:
                await using (var revoke = new NpgsqlCommand(
                    "UPDATE player_credentials SET revoked_at = $2 WHERE player_id = $1 AND revoked_at IS NULL", connection, transaction))
                {
                    revoke.Parameters.AddWithValue(playerId);
                    revoke.Parameters.AddWithValue(now);
                    await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new RotationResult(status, playerId, default);
        }

        await using (var replace = new NpgsqlCommand(
            "UPDATE player_credentials SET replaced_at = $2, replaced_by = $3 WHERE credential_hash = $1", connection, transaction))
        {
            replace.Parameters.AddWithValue(oldHash);
            replace.Parameters.AddWithValue(now);
            replace.Parameters.AddWithValue(successorHash);
            await replace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertCredentialAsync(connection, transaction, successorHash, playerId, successorExpiresAt, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RotationResult(RotationStatus.Rotated, playerId, successorExpiresAt);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM player_credentials
            WHERE credential_hash IN (SELECT credential_hash FROM player_credentials WHERE expires_at < $1 LIMIT $2)
            """);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCredentialAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        byte[] hash, Guid playerId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO player_credentials (credential_hash, player_id, expires_at) VALUES ($1, $2, $3)", connection, transaction);
        insert.Parameters.AddWithValue(hash);
        insert.Parameters.AddWithValue(playerId);
        insert.Parameters.AddWithValue(expiresAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
