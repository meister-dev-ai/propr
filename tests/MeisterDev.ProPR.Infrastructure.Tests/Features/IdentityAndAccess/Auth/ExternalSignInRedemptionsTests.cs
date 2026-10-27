// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.Auth;

/// <summary>
///     The record of the one-time values of external sign-ins in PostgreSQL: each value is accepted once, also when two
///     requests carry it at the same time, and the migration adds the record and the mark of verified links.
/// </summary>
[Collection("PostgresIntegration4")]
public sealed class ExternalSignInRedemptionsTests(PostgresContainerFixture fixture)
{
    private const string Migration = "AddExternalSignInRedemptionsAndVerifiedIssuers";
    private const string PreviousMigration = "AddPlatformAdminElevations";

    [Fact]
    public async Task TryRedeem_AcceptsAValueOnce()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        await using var context = CreateContext(database.ConnectionString);
        var redemptions = new ExternalSignInRedemptions(context);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        var first = await redemptions.TryRedeemAsync("state-1", expiresAt, CancellationToken.None);
        var second = await redemptions.TryRedeemAsync("state-1", expiresAt, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.True(await redemptions.IsRedeemedAsync("state-1", CancellationToken.None));
        Assert.False(await redemptions.IsRedeemedAsync("state-2", CancellationToken.None));
    }

    /// <summary>
    ///     The first request has written the value but not committed it when the second one looks for it, so the second
    ///     one finds nothing and writes the value as well. The primary key makes it wait for the first and then refuses
    ///     it, and the second request reports the value as used.
    /// </summary>
    [Fact]
    public async Task TryRedeem_OfTheSameValueByTwoRequestsAtOnce_AcceptsItForOneRequest()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        await using var first = CreateContext(database.ConnectionString);
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        Assert.True(await new ExternalSignInRedemptions(first).TryRedeemAsync("state-1", expiresAt, CancellationToken.None));
        await using var second = CreateContext(database.ConnectionString);

        var secondRedemption = new ExternalSignInRedemptions(second).TryRedeemAsync("state-1", expiresAt, CancellationToken.None);
        await WaitUntilASessionWaitsForALockAsync(database.ConnectionString);
        await firstTransaction.CommitAsync();

        Assert.False(await secondRedemption);
        Assert.Equal(
            1,
            await CountAsync(database.ConnectionString, "SELECT count(*)::int FROM external_sign_in_redemptions"));
    }

    /// <summary>
    ///     Two requests redeem different values while an expired record waits for its removal. The first removes the
    ///     record and keeps it locked until it commits; the second waits for it, then finds the record gone. The second
    ///     request accepts its own value all the same.
    /// </summary>
    [Fact]
    public async Task TryRedeem_WhileAnotherRequestRemovesTheSameExpiredRecord_AcceptsItsOwnValue()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        await ExecuteAsync(
            database.ConnectionString,
            "INSERT INTO external_sign_in_redemptions (redemption_key, expires_at) VALUES ('expired-record', now() - interval '1 minute')");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        await using var first = CreateContext(database.ConnectionString);
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        Assert.True(await new ExternalSignInRedemptions(first).TryRedeemAsync("state-1", expiresAt, CancellationToken.None));
        await using var second = CreateContext(database.ConnectionString);

        var secondRedemption = new ExternalSignInRedemptions(second).TryRedeemAsync("state-2", expiresAt, CancellationToken.None);
        await WaitUntilASessionWaitsForALockAsync(database.ConnectionString);
        await firstTransaction.CommitAsync();

        Assert.True(await secondRedemption);
        Assert.Equal(
            2,
            await CountAsync(database.ConnectionString, "SELECT count(*)::int FROM external_sign_in_redemptions"));
    }

    [Fact]
    public async Task TryRedeem_RemovesTheRecordsOfExpiredValues()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        await using var context = CreateContext(database.ConnectionString);
        var redemptions = new ExternalSignInRedemptions(context);
        Assert.True(await redemptions.TryRedeemAsync("expired", DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None));

        Assert.True(await redemptions.TryRedeemAsync("current", DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None));

        Assert.False(await redemptions.IsRedeemedAsync("expired", CancellationToken.None));
        Assert.True(await redemptions.IsRedeemedAsync("current", CancellationToken.None));
    }

    /// <summary>
    ///     Links that existed before the migration count as not verified until their next sign-in. The downgrade removes
    ///     the record of one-time values and the mark.
    /// </summary>
    [Fact]
    public async Task Upgrade_MarksExistingLinksAsNotVerifiedAndDowngradeRemovesTheRedemptions()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(PreviousMigration);
        var tenantId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var before = CreateContext(database.ConnectionString))
        {
            var now = DateTimeOffset.UtcNow;
            await before.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
                 VALUES ({tenantId}, {tenantId.ToString("N")}, 'Acme', true, {now}, {now});
                 INSERT INTO tenant_sso_providers
                   (id, tenant_id, display_name, provider_kind, protocol_kind, client_id, scopes, allowed_email_domains,
                    is_enabled, auto_create_users, created_at, updated_at)
                 VALUES ({providerId}, {tenantId}, 'Acme Google', 'Google', 'Oidc', 'client', '[]'::jsonb, '[]'::jsonb,
                   true, true, {now}, {now});
                 INSERT INTO app_users (id, username, global_role, is_active, created_at)
                 VALUES ({userId}, {"user-" + userId.ToString("N")}, 'User', true, {now});
                 INSERT INTO external_identities
                   (id, tenant_id, user_id, sso_provider_id, issuer, subject, email, email_verified, created_at)
                 VALUES ({Guid.NewGuid()}, {tenantId}, {userId}, {providerId}, 'https://accounts.google.com', 'subject-1',
                   'user@acme.test', true, {now})
                 """);
        }

        await using var after = CreateContext(database.ConnectionString);
        await after.Database.MigrateAsync();
        Assert.False(after.Database.HasPendingModelChanges());
        var link = await after.ExternalIdentities.AsNoTracking().SingleAsync(identity => identity.UserId == userId);
        Assert.False(link.IssuerVerified);
        Assert.True(await new ExternalSignInRedemptions(after).TryRedeemAsync("state-1", DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None));

        await after.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreviousMigration);

        Assert.Equal(
            0,
            await CountAsync(
                database.ConnectionString,
                "SELECT count(*)::int FROM information_schema.tables WHERE table_name = 'external_sign_in_redemptions'"));
        Assert.Equal(
            0,
            await CountAsync(
                database.ConnectionString,
                "SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'external_identities' AND column_name = 'issuer_verified'"));
    }

    private async Task<TestDatabase> CreateDatabaseAsync(string migration)
    {
        var name = "propr_sign_in_redemptions_" + Guid.NewGuid().ToString("N");
        await fixture.CreateDatabaseAtMigrationAsync(name, migration);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = name,
            Pooling = false,
        }.ConnectionString;
        return new TestDatabase(fixture.ConnectionString, name, connectionString);
    }

    /// <summary>Waits until a session of the database waits for a lock that another transaction holds.</summary>
    private static async Task WaitUntilASessionWaitsForALockAsync(string connectionString)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var waiting = await CountAsync(
                connectionString,
                "SELECT count(*)::int FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The second redemption did not wait for the lock of the first one.");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static MeisterProPRDbContext CreateContext(string connectionString)
    {
        return new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
                .Options);
    }

    /// <summary>A database of one test, dropped when the test is done with it.</summary>
    private sealed class TestDatabase(string serverConnectionString, string name, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(serverConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }
}
