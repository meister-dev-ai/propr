// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

/// <summary>
///     The provisioning of users in PostgreSQL: the schema of the provisioning records, the end of the sessions and
///     tokens of a user in the transaction that removes its last membership, and two requests for the same subject at
///     once.
/// </summary>
[Collection("PostgresIntegration4")]
public sealed class ProvisionedUserServicePostgresTests(PostgresContainerFixture fixture)
{
    private const string Migration = "AddProvisionedUsers";
    private const string PreviousMigration = "AddExternalSignInRedemptionsAndVerifiedIssuers";
    private const string Issuer = "https://id.example.test/realms/customers";

    [Fact]
    public async Task Upgrade_AddsOneProvisioningRecordPerIssuerAndSubjectAndDowngradeRemovesThem()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(PreviousMigration);
        await using var context = CreateContext(database.ConnectionString);
        await context.Database.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());
        var first = await InsertAccountAsync(context);
        var second = await InsertAccountAsync(context);
        await InsertProvisioningRecordAsync(context, first, "subject-1");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertProvisioningRecordAsync(context, second, "subject-1"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM app_users WHERE id = {first}");
        Assert.Equal(0, await CountAsync(database.ConnectionString, "SELECT count(*)::int FROM provisioned_users"));

        await context.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreviousMigration);
        Assert.Equal(
            0,
            await CountAsync(
                database.ConnectionString,
                "SELECT count(*)::int FROM information_schema.tables WHERE table_name = 'provisioned_users'"));
    }

    [Fact]
    public async Task RemoveMembership_OfTheLastMembership_DisablesTheAccountAndRevokesItsRefreshTokensAndPersonalAccessTokens()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        await using var context = CreateContext(database.ConnectionString);
        var (tenantId, clientId) = await InsertTenantWithClientAsync(context);
        var service = CreateService(context);
        var user = (await service.PutUserAsync("subject-1", "person@customer.test", null, null)).Value!;
        Assert.True((await service.PutMembershipAsync(tenantId, "subject-1", [clientId], null)).IsSuccess);
        await new RefreshTokenRepository(context, Policy()).AddAsync(
            new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.UserId,
                TokenHash = "refresh-token-hash",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow,
            });
        await new UserPatRepository(context).AddAsync(
            new UserPat
            {
                Id = Guid.NewGuid(),
                UserId = user.UserId,
                TokenHash = "pat-hash",
                TokenLookupHash = "pat-lookup-hash",
                Label = "browser extension",
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var removal = await service.RemoveMembershipAsync(tenantId, "subject-1", null);

        Assert.True(removal.IsSuccess);
        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM app_users WHERE id = '{user.UserId}' AND is_active"));
        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM refresh_tokens WHERE user_id = '{user.UserId}' AND revoked_at IS NULL"));
        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM user_pats WHERE user_id = '{user.UserId}' AND NOT is_revoked"));
        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM user_client_roles WHERE user_id = '{user.UserId}'"));
    }

    /// <summary>
    ///     The platform removes a person from two organizations at the same time. The removal from the first tenant has
    ///     deleted its membership but not committed it when the removal from the second tenant reads the memberships of
    ///     the user. The lock of the user makes the second removal wait, so it reads that no membership remains, disables
    ///     the account and revokes its tokens. Without the lock, each removal reads the membership that the other one
    ///     removes, and the account ends active without any membership and with valid tokens.
    /// </summary>
    [Fact]
    public async Task RemoveMembership_InTwoTenantsAtOnce_DisablesTheAccountAndRevokesItsTokens()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        Guid userId;
        Guid firstTenant;
        Guid secondTenant;
        await using (var setup = CreateContext(database.ConnectionString))
        {
            var (tenantA, clientA) = await InsertTenantWithClientAsync(setup);
            var (tenantB, clientB) = await InsertTenantWithClientAsync(setup);
            (firstTenant, secondTenant) = (tenantA, tenantB);
            var service = CreateService(setup);
            userId = (await service.PutUserAsync("subject-1", "person@customer.test", null, null)).Value!.UserId;
            Assert.True((await service.PutMembershipAsync(tenantA, "subject-1", [clientA], null)).IsSuccess);
            Assert.True((await service.PutMembershipAsync(tenantB, "subject-1", [clientB], null)).IsSuccess);
            await SeedTokensAsync(setup, userId);
        }

        await using var first = CreateContext(database.ConnectionString);
        var firstSession = await OpenSessionAsync(first);
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        Assert.True((await CreateService(first).RemoveMembershipAsync(firstTenant, "subject-1", null)).IsSuccess);
        await using var second = CreateContext(database.ConnectionString);
        var secondSession = await OpenSessionAsync(second);

        var secondRemoval = CreateService(second).RemoveMembershipAsync(secondTenant, "subject-1", null);
        await WaitUntilBlockedByAsync(database.ConnectionString, secondSession, firstSession, secondRemoval);
        await firstTransaction.CommitAsync();
        Assert.True((await secondRemoval).IsSuccess);

        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM tenant_memberships WHERE user_id = '{userId}'"));
        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM app_users WHERE id = '{userId}' AND is_active"));
        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM refresh_tokens WHERE user_id = '{userId}' AND revoked_at IS NULL"));
        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM user_pats WHERE user_id = '{userId}' AND NOT is_revoked"));
    }

    /// <summary>
    ///     The platform removes the last membership of a person and sets a membership in another tenant at the same time.
    ///     The removal has disabled the account but not committed it when the new membership starts. The lock of the user
    ///     makes the new membership wait and read the account after the removal, so it enables the account again. A
    ///     membership that read the account before the removal committed would leave the account disabled.
    /// </summary>
    [Fact]
    public async Task PutMembership_WhileTheLastOtherMembershipIsRemoved_LeavesTheAccountActive()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        Guid userId;
        Guid removedTenant;
        Guid joinedTenant;
        Guid joinedClient;
        await using (var setup = CreateContext(database.ConnectionString))
        {
            var (tenantA, clientA) = await InsertTenantWithClientAsync(setup);
            var (tenantB, clientB) = await InsertTenantWithClientAsync(setup);
            (removedTenant, joinedTenant, joinedClient) = (tenantA, tenantB, clientB);
            var service = CreateService(setup);
            userId = (await service.PutUserAsync("subject-1", "person@customer.test", null, null)).Value!.UserId;
            Assert.True((await service.PutMembershipAsync(tenantA, "subject-1", [clientA], null)).IsSuccess);
        }

        await using var first = CreateContext(database.ConnectionString);
        var firstSession = await OpenSessionAsync(first);
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        Assert.True((await CreateService(first).RemoveMembershipAsync(removedTenant, "subject-1", null)).IsSuccess);
        await using var second = CreateContext(database.ConnectionString);
        var secondSession = await OpenSessionAsync(second);

        var membership = CreateService(second).PutMembershipAsync(joinedTenant, "subject-1", [joinedClient], null);
        await WaitUntilBlockedByAsync(database.ConnectionString, secondSession, firstSession, membership);
        await firstTransaction.CommitAsync();
        Assert.True((await membership).IsSuccess);

        Assert.Equal(
            1,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM tenant_memberships WHERE user_id = '{userId}'"));
        Assert.Equal(
            1,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM app_users WHERE id = '{userId}' AND is_active"));
    }

    /// <summary>
    ///     A platform administrator disables the account of a provisioned user while the platform removes its last
    ///     membership. The removal has disabled the account for the lack of a membership but not committed it when the
    ///     administrator's decision is written. The decision waits for the removal and replaces the state that the
    ///     removal recorded, so a membership that the platform sets later leaves the account disabled.
    /// </summary>
    [Fact]
    public async Task SetActive_WhileThePlatformRemovesTheLastMembership_KeepsTheDecisionOfTheAdministrator()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        Guid userId;
        Guid tenantId;
        Guid clientId;
        await using (var setup = CreateContext(database.ConnectionString))
        {
            (tenantId, clientId) = await InsertTenantWithClientAsync(setup);
            var service = CreateService(setup);
            userId = (await service.PutUserAsync("subject-1", "person@customer.test", null, null)).Value!.UserId;
            Assert.True((await service.PutMembershipAsync(tenantId, "subject-1", [clientId], null)).IsSuccess);
        }

        await using var platform = CreateContext(database.ConnectionString);
        var platformSession = await OpenSessionAsync(platform);
        await using var removal = await platform.Database.BeginTransactionAsync();
        Assert.True((await CreateService(platform).RemoveMembershipAsync(tenantId, "subject-1", null)).IsSuccess);
        await using var administrator = CreateContext(database.ConnectionString);
        var administratorSession = await OpenSessionAsync(administrator);
        var users = new AppUserRepository(administrator);

        // The users endpoint reads the account before it writes the decision of the administrator.
        Assert.True((await users.GetByIdAsync(userId))!.IsActive);
        var decision = users.SetActiveAsync(userId, false);
        await WaitUntilBlockedByAsync(database.ConnectionString, administratorSession, platformSession, decision);
        await removal.CommitAsync();
        await decision;
        await using (var later = CreateContext(database.ConnectionString))
        {
            Assert.True((await CreateService(later).PutMembershipAsync(tenantId, "subject-1", [clientId], null)).IsSuccess);
        }

        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM app_users WHERE id = '{userId}' AND is_active"));
        Assert.Equal(
            0,
            await CountAsync(
                database.ConnectionString,
                $"SELECT count(*)::int FROM provisioned_users WHERE user_id = '{userId}' AND inactive_without_membership_since IS NOT NULL"));
    }

    /// <summary>
    ///     The platform removes the last membership of a provisioned user while a platform administrator disables the
    ///     account. The decision of the administrator is written but not committed when the removal starts. The lock of the
    ///     user makes the removal wait and read the decision, so the removal records no inactive state of its own, and a
    ///     membership that the platform sets later leaves the account disabled. Without the lock in the path of the
    ///     administrator, the removal reads the account before the decision commits and records its own state after it.
    /// </summary>
    [Fact]
    public async Task RemoveMembership_WhileAnAdministratorDisablesTheAccount_KeepsTheDecisionOfTheAdministrator()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        Guid userId;
        Guid tenantId;
        Guid clientId;
        await using (var setup = CreateContext(database.ConnectionString))
        {
            (tenantId, clientId) = await InsertTenantWithClientAsync(setup);
            var service = CreateService(setup);
            userId = (await service.PutUserAsync("subject-1", "person@customer.test", null, null)).Value!.UserId;
            Assert.True((await service.PutMembershipAsync(tenantId, "subject-1", [clientId], null)).IsSuccess);
        }

        await using var administrator = CreateContext(database.ConnectionString);
        var administratorSession = await OpenSessionAsync(administrator);
        await using var decision = await administrator.Database.BeginTransactionAsync();
        var users = new AppUserRepository(administrator);
        Assert.True((await users.GetByIdAsync(userId))!.IsActive);
        await users.SetActiveAsync(userId, false);
        await using var platform = CreateContext(database.ConnectionString);
        var platformSession = await OpenSessionAsync(platform);

        var removal = CreateService(platform).RemoveMembershipAsync(tenantId, "subject-1", null);
        await WaitUntilBlockedByAsync(database.ConnectionString, platformSession, administratorSession, removal);
        await decision.CommitAsync();
        Assert.True((await removal).IsSuccess);
        await using (var later = CreateContext(database.ConnectionString))
        {
            Assert.True((await CreateService(later).PutMembershipAsync(tenantId, "subject-1", [clientId], null)).IsSuccess);
        }

        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM app_users WHERE id = '{userId}' AND is_active"));
        Assert.Equal(
            0,
            await CountAsync(
                database.ConnectionString,
                $"SELECT count(*)::int FROM provisioned_users WHERE user_id = '{userId}' AND inactive_without_membership_since IS NOT NULL"));
    }

    /// <summary>
    ///     The first request has created the user but not committed it when the second one looks for it, so the second
    ///     one creates the user as well. A unique index makes the second request wait for the first and then refuses its
    ///     account, and the second request reads the user of the first one and answers with it.
    /// </summary>
    [Fact]
    public async Task PutUser_ForTheSameSubjectByTwoRequestsAtOnce_KeepsOneAccount()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync(Migration);
        await using var first = CreateContext(database.ConnectionString);
        var firstSession = await OpenSessionAsync(first);
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        var created = await CreateService(first).PutUserAsync("subject-1", "person@customer.test", "Person", null);
        Assert.Equal(ProvisioningOutcome.Created, created.Outcome);
        await using var second = CreateContext(database.ConnectionString);
        var secondSession = await OpenSessionAsync(second);

        var secondRequest = CreateService(second).PutUserAsync("subject-1", "person@customer.test", "Person", null);
        await WaitUntilBlockedByAsync(database.ConnectionString, secondSession, firstSession);
        await firstTransaction.CommitAsync();
        var answer = await secondRequest;

        Assert.Equal(ProvisioningOutcome.Succeeded, answer.Outcome);
        Assert.Equal(created.Value!.UserId, answer.Value!.UserId);
        Assert.Equal(1, await CountAsync(database.ConnectionString, "SELECT count(*)::int FROM provisioned_users"));
        Assert.Equal(1, await CountAsync(database.ConnectionString, "SELECT count(*)::int FROM app_users"));
    }

    private static ProvisionedUserService CreateService(MeisterProPRDbContext context)
    {
        return new ProvisionedUserService(
            context,
            new AppUserRepository(context),
            new RefreshTokenRepository(context, Policy()),
            new UserPatRepository(context),
            Substitute.For<IUserAccountAuditLog>(),
            Microsoft.Extensions.Options.Options.Create(
                new ProvisionedUserSsoOptions
                {
                    Authority = Issuer,
                    ClientId = "propr-cell",
                    ClientSecret = "cell-client-secret",
                }));
    }

    private static async Task SeedTokensAsync(MeisterProPRDbContext context, Guid userId)
    {
        await new RefreshTokenRepository(context, Policy()).AddAsync(
            new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "refresh-token-hash-" + userId.ToString("N"),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow,
            });
        await new UserPatRepository(context).AddAsync(
            new UserPat
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "pat-hash",
                TokenLookupHash = "pat-lookup-hash-" + userId.ToString("N"),
                Label = "browser extension",
                CreatedAt = DateTimeOffset.UtcNow,
            });
    }

    private static SessionPolicy Policy()
    {
        return new SessionPolicy { IdleTimeout = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromHours(72) };
    }

    private static async Task<Guid> InsertAccountAsync(MeisterProPRDbContext context)
    {
        var userId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO app_users (id, username, global_role, is_active, created_at)
             VALUES ({userId}, {"user-" + userId.ToString("N")}, 'User', false, {DateTimeOffset.UtcNow})
             """);
        return userId;
    }

    private static Task<int> InsertProvisioningRecordAsync(MeisterProPRDbContext context, Guid userId, string subject)
    {
        var now = DateTimeOffset.UtcNow;
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO provisioned_users (user_id, issuer, subject, email, created_at, updated_at)
             VALUES ({userId}, {Issuer}, {subject}, 'person@customer.test', {now}, {now})
             """);
    }

    private static async Task<(Guid TenantId, Guid ClientId)> InsertTenantWithClientAsync(MeisterProPRDbContext context)
    {
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
             VALUES ({tenantId}, {tenantId.ToString("N")}, 'Customer organization', true, {now}, {now});
             """);
        context.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = tenantId,
                DisplayName = "Workspace",
                IsActive = true,
                CreatedAt = now,
            });
        await context.SaveChangesAsync();
        return (tenantId, clientId);
    }

    private async Task<TestDatabase> CreateDatabaseAsync(string migration)
    {
        var name = "propr_provisioned_users_" + Guid.NewGuid().ToString("N");
        await fixture.CreateDatabaseAtMigrationAsync(name, migration);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = name,
            Pooling = false,
        }.ConnectionString;
        return new TestDatabase(fixture.ConnectionString, name, connectionString);
    }

    /// <summary>
    ///     Opens the connection of the context, which then serves every later command of the context, and returns the
    ///     process ID of its session in PostgreSQL.
    /// </summary>
    private static async Task<int> OpenSessionAsync(MeisterProPRDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        return await context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
    }

    /// <summary>
    ///     Waits until the session <paramref name="waiting" /> waits for a lock that the session <paramref name="blocking" />
    ///     holds. When <paramref name="request" /> is given, the wait also ends when the request has finished without
    ///     waiting.
    /// </summary>
    private static async Task WaitUntilBlockedByAsync(string connectionString, int waiting, int blocking, Task? request = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (request is { IsCompleted: true } || await IsBlockedByAsync(connectionString, waiting, blocking))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"The session {waiting} did not wait for a lock of the session {blocking}.");
    }

    private static async Task<bool> IsBlockedByAsync(string connectionString, int waiting, int blocking)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT @blocking = ANY(pg_blocking_pids(@waiting))", connection);
        command.Parameters.AddWithValue("waiting", waiting);
        command.Parameters.AddWithValue("blocking", blocking);
        return (bool)(await command.ExecuteScalarAsync())!;
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
