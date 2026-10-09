// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Data;

[Collection("PostgresIntegration1")]
public sealed class PostgresAdvisoryLocksTests(PostgresContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransactionLockUsesExistingIdentityAndEndsWithTransaction(bool commit)
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()).Options;
        await using var db = new MeisterProPRDbContext(options);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        var key = $"lock-test:{Guid.NewGuid():N}";
        await using var transaction = await db.Database.BeginTransactionAsync();
        await PostgresAdvisoryLocks.AcquireTransactionAsync(db, key, CancellationToken.None);
        Assert.False(await TryAcquireAsync(observer, key));
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        Assert.True(await TryAcquireAsync(observer, key));
        await UnlockAsync(observer, key);
    }

    [Fact]
    public async Task SessionLeaseCancellationDoesNotReleaseAnotherOwnerAndDisposalReleasesOnlyItsKey()
    {
        fixture.SkipIfUnavailable();
        await using var source = new NpgsqlConnection(fixture.ConnectionString);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        var key = $"lock-test:{Guid.NewGuid():N}";
        var lease = await PostgresAdvisoryLocks.AcquireSessionAsync(source, key, "lock-test", CancellationToken.None);
        try
        {
            Assert.False(await TryAcquireAsync(observer, key));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PostgresAdvisoryLocks.AcquireSessionAsync(source, key, "lock-test", cancellation.Token));
            Assert.False(await TryAcquireAsync(observer, key));
            var independentKey = $"lock-test:{Guid.NewGuid():N}";
            await using var independent = await PostgresAdvisoryLocks.AcquireSessionAsync(source, independentKey, "lock-test", CancellationToken.None);
            Assert.False(await TryAcquireAsync(observer, independentKey));
        }
        finally
        {
            await lease.DisposeAsync();
        }

        await lease.DisposeAsync();
        Assert.True(await TryAcquireAsync(observer, key));
        await UnlockAsync(observer, key);
    }

    [Theory]
    [InlineData("bigint", true)]
    [InlineData("bigint", false)]
    [InlineData("hashtext", true)]
    [InlineData("hashtext", false)]
    [InlineData("scoped", true)]
    [InlineData("scoped", false)]
    public async Task AdditionalTransactionProtocolsMatchOriginalSqlAndReleaseWithTransaction(string protocol, bool commit)
    {
        fixture.SkipIfUnavailable();
        await using var db = this.CreateContext();
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        var key = Guid.NewGuid().ToString("N");
        var numericKey = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);
        await using var transaction = await db.Database.BeginTransactionAsync();

        await AcquireProtocolAsync(db, protocol, key, numericKey, 1, CancellationToken.None);

        Assert.False(await ProbeProtocolAsync(observer, protocol, key, numericKey, 1));
        Assert.True(await ProbeProtocolAsync(observer, protocol, Guid.NewGuid().ToString("N"), numericKey ^ 1, 1));
        if (protocol == "scoped")
        {
            Assert.True(await ProbeProtocolAsync(observer, protocol, key, numericKey, 2));
        }

        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        Assert.True(await ProbeProtocolAsync(observer, protocol, key, numericKey, 1));
    }

    [Theory]
    [InlineData("bigint")]
    [InlineData("hashtext")]
    [InlineData("scoped")]
    public async Task AdditionalTransactionProtocolsWaitForOriginalSqlAndCancellationPreservesOwner(string protocol)
    {
        fixture.SkipIfUnavailable();
        await using var db = this.CreateContext();
        await using var owner = new NpgsqlConnection(fixture.ConnectionString);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await owner.OpenAsync();
        await observer.OpenAsync();
        var key = Guid.NewGuid().ToString("N");
        var numericKey = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);
        await using var hold = OriginalProtocolCommand(owner, protocol, "pg_advisory_lock", key, numericKey, 1);
        await hold.ExecuteNonQueryAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AcquireProtocolAsync(db, protocol, key, numericKey, 1, cancellation.Token));

            Assert.False(await ProbeProtocolAsync(observer, protocol, key, numericKey, 1));
            await transaction.RollbackAsync();
        }
        finally
        {
            await using var release = OriginalProtocolCommand(owner, protocol, "pg_advisory_unlock", key, numericKey, 1);
            Assert.True((bool)(await release.ExecuteScalarAsync())!);
        }

        await using var nextTransaction = await db.Database.BeginTransactionAsync();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await AcquireProtocolAsync(db, protocol, key, numericKey, 1, bound.Token);
        Assert.False(await ProbeProtocolAsync(observer, protocol, key, numericKey, 1));
    }

    [Fact]
    public async Task ScopedAndBigintLocksWithMatchingBitsRemainIndependent()
    {
        fixture.SkipIfUnavailable();
        await using var scoped = this.CreateContext();
        await using var numeric = this.CreateContext();
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        var key = Guid.NewGuid().ToString("N");
        await using var hashCommand = new NpgsqlCommand("SELECT hashtext(@key)", observer);
        hashCommand.Parameters.AddWithValue("key", key);
        var hash = (int)(await hashCommand.ExecuteScalarAsync())!;
        const int scope = 1;
        var numericKey = ((long)scope << 32) | (uint)hash;
        await using var scopedTransaction = await scoped.Database.BeginTransactionAsync();
        await using var numericTransaction = await numeric.Database.BeginTransactionAsync();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await PostgresAdvisoryLocks.AcquireScopedHashTextTransactionAsync(scoped, scope, key, bound.Token);
        await PostgresAdvisoryLocks.AcquireTransactionAsync(numeric, numericKey, bound.Token);

        Assert.False(await ProbeProtocolAsync(observer, "scoped", key, numericKey, scope));
        Assert.False(await ProbeProtocolAsync(observer, "bigint", key, numericKey, scope));
        await scopedTransaction.RollbackAsync();
        Assert.True(await ProbeProtocolAsync(observer, "scoped", key, numericKey, scope));
        Assert.False(await ProbeProtocolAsync(observer, "bigint", key, numericKey, scope));
        await numericTransaction.RollbackAsync();
        Assert.True(await ProbeProtocolAsync(observer, "bigint", key, numericKey, scope));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(0x4D50_524C_4943_4E53L)]
    public async Task BigintTransactionPreservesFullSignedKey(long key)
    {
        fixture.SkipIfUnavailable();
        await using var db = this.CreateContext();
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await PostgresAdvisoryLocks.AcquireTransactionAsync(db, key, CancellationToken.None);

        Assert.False(await ProbeProtocolAsync(observer, "bigint", "", key, 1));
        Assert.True(await ProbeProtocolAsync(observer, "bigint", "", key ^ 1, 1));
    }

    private MeisterProPRDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()).Options);

    private static Task AcquireProtocolAsync(MeisterProPRDbContext db, string protocol, string key, long numericKey, int scope, CancellationToken ct) =>
        protocol switch
        {
            "bigint" => PostgresAdvisoryLocks.AcquireTransactionAsync(db, numericKey, ct),
            "hashtext" => PostgresAdvisoryLocks.AcquireHashTextTransactionAsync(db, key, ct),
            "scoped" => PostgresAdvisoryLocks.AcquireScopedHashTextTransactionAsync(db, scope, key, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };

    private static async Task<bool> ProbeProtocolAsync(NpgsqlConnection connection, string protocol, string key, long numericKey, int scope)
    {
        await using var command = OriginalProtocolCommand(connection, protocol, "pg_try_advisory_xact_lock", key, numericKey, scope);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static NpgsqlCommand OriginalProtocolCommand(NpgsqlConnection connection, string protocol, string function, string key, long numericKey, int scope)
    {
        var arguments = protocol switch
        {
            "bigint" => "@numericKey",
            "hashtext" => "hashtext(@key)",
            "scoped" => "@scope, hashtext(@key)",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
        var command = new NpgsqlCommand($"SELECT {function}({arguments})", connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("numericKey", numericKey);
        command.Parameters.AddWithValue("scope", scope);
        return command;
    }

    private static async Task<bool> TryAcquireAsync(NpgsqlConnection connection, string key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", connection);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task UnlockAsync(NpgsqlConnection connection, string key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
    }
}
