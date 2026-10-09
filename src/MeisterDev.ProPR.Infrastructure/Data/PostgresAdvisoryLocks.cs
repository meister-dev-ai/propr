// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Data;

/// <summary>PostgreSQL advisory lock execution with separate transaction and session ownership.</summary>
public static class PostgresAdvisoryLocks
{
    /// <summary>Acquires a bigint advisory lock until the caller's transaction ends.</summary>
    public static Task<int> AcquireTransactionAsync(MeisterProPRDbContext db, long key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);

    /// <summary>Acquires a transaction lock using the existing 32-bit hashtext value in the bigint key space.</summary>
    internal static Task<int> AcquireHashTextTransactionAsync(MeisterProPRDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", ct);

    /// <summary>Acquires a transaction lock using the scope and 32-bit hashtext value in the two-int key space.</summary>
    internal static Task<int> AcquireScopedHashTextTransactionAsync(MeisterProPRDbContext db, int scope, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({scope}, hashtext({key}))", ct);

    internal static Task AcquireTransactionAsync(MeisterProPRDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

    internal static async Task<IAsyncDisposable> AcquireSessionAsync(NpgsqlConnection source, string key, string applicationNameSuffix, CancellationToken ct)
    {
        var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            ApplicationName = $"{new NpgsqlConnectionStringBuilder(source.ConnectionString).ApplicationName}:{applicationNameSuffix}",
            MinPoolSize = 0,
            MaxPoolSize = 16,
            Pooling = true,
            Timeout = 30,
            Multiplexing = false,
        };
        var connection = source.CloneWith(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", connection) { CommandTimeout = 30 };
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new SessionLease(connection, key);
        }
        catch
        {
            await new SessionLease(connection, key).DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class SessionLease(NpgsqlConnection connection, string key) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            try
            {
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    return;
                }

                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", connection) { CommandTimeout = 10 };
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                NpgsqlConnection.ClearPool(connection);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
