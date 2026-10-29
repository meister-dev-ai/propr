// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

/// <summary>
///     The use of a pending tenant link in PostgreSQL. The redemption of the pending link and the account, membership and
///     link that follow are written together, so a failure in between leaves the pending link usable and nothing of the
///     account or the link behind.
/// </summary>
[Collection("PostgresIntegration4")]
public sealed class TenantPendingLinkTransactionTests(PostgresContainerFixture fixture)
{
    private const string Issuer = "https://accounts.google.com";

    // The services under test read the tenant record, so the database carries every migration. A database stopped at
    // an older migration lacks the columns that later migrations added to the tables these services read.
    private static readonly string Migration = LatestMigration();

    private readonly SecretProtectionCodec _codec = new(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task CreateAccount_WhenTheLinkCannotBeWritten_LeavesThePendingLinkUsableAndNoAccountBehind()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync();
        var (tenantId, providerId) = await SeedTenantWithProviderAsync(database.ConnectionString);
        var email = $"person.{Guid.NewGuid():N}@acme.test";
        var pendingLink = this.Protect(tenantId, providerId, email);
        await FailLinkInsertsAsync(database.ConnectionString);

        await using (var context = CreateContext(database.ConnectionString))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => this.CreateService(context).CreateAccountForPendingLinkAsync(pendingLink));
        }

        Assert.Equal(0, await CountAccountsAsync(database.ConnectionString, email));
        await AllowLinkInsertsAsync(database.ConnectionString);
        await using (var context = CreateContext(database.ConnectionString))
        {
            var service = this.CreateService(context);
            Assert.NotNull(await service.GetPendingLinkAsync(pendingLink));

            var outcome = await service.CreateAccountForPendingLinkAsync(pendingLink);

            Assert.NotNull(outcome.User);
        }

        Assert.Equal(1, await CountAccountsAsync(database.ConnectionString, email));
    }

    [Fact]
    public async Task Confirm_WhenTheLinkCannotBeWritten_LeavesThePendingLinkUsableAndNoMembershipBehind()
    {
        fixture.SkipIfUnavailable();
        await using var database = await this.CreateDatabaseAsync();
        var (tenantId, providerId) = await SeedTenantWithProviderAsync(database.ConnectionString);
        var accountId = Guid.NewGuid();
        await ExecuteAsync(
            database.ConnectionString,
            $"INSERT INTO app_users (id, username, global_role, is_active, created_at) VALUES ('{accountId}', 'user-{accountId:N}', 'User', true, now())");
        var pendingLink = this.Protect(tenantId, providerId, $"person.{Guid.NewGuid():N}@acme.test");
        await FailLinkInsertsAsync(database.ConnectionString);

        await using (var context = CreateContext(database.ConnectionString))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => this.CreateService(context).ConfirmPendingLinkAsync(pendingLink, accountId));
        }

        Assert.Equal(
            0,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM tenant_memberships WHERE user_id = '{accountId}'"));
        await AllowLinkInsertsAsync(database.ConnectionString);
        await using (var context = CreateContext(database.ConnectionString))
        {
            var outcome = await this.CreateService(context).ConfirmPendingLinkAsync(pendingLink, accountId);

            Assert.Equal(accountId, outcome.User?.Id);
        }

        Assert.Equal(
            1,
            await CountAsync(database.ConnectionString, $"SELECT count(*)::int FROM external_identities WHERE user_id = '{accountId}'"));
    }

    private string Protect(Guid tenantId, Guid providerId, string email)
    {
        var pendingLink = new TenantAuthService.TenantPendingLink(
            AuthorizationCodeFlow.CreateRandomValue(),
            tenantId,
            providerId,
            Issuer,
            $"subject-{Guid.NewGuid():N}",
            email,
            true,
            TenantExternalSignInMode.SignIn,
            DateTimeOffset.UtcNow);
        return ProtectedSignInState.Protect(this._codec, pendingLink, TenantAuthService.PendingLinkPurpose);
    }

    private TenantAuthService CreateService(MeisterProPRDbContext context)
    {
        return new TenantAuthService(
            context,
            new AppUserRepository(context),
            Substitute.For<IPasswordHashService>(),
            this._codec,
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IOidcTokenValidator>(),
            Substitute.For<IAccountLockoutService>(),
            new ExternalSignInRedemptions(context),
            NullLogger<TenantAuthService>.Instance);
    }

    private static async Task<(Guid TenantId, Guid ProviderId)> SeedTenantWithProviderAsync(string connectionString)
    {
        var tenantId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            $"""
             INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
             VALUES ('{tenantId}', '{tenantId:N}', 'Acme', true, now(), now());
             INSERT INTO tenant_sso_providers
               (id, tenant_id, display_name, provider_kind, protocol_kind, client_id, scopes, allowed_email_domains,
                is_enabled, auto_create_users, created_at, updated_at)
             VALUES ('{providerId}', '{tenantId}', 'Acme Google', 'Google', 'Oidc', 'client', '[]'::jsonb,
               '["acme.test"]'::jsonb, true, true, now(), now());
             """);
        return (tenantId, providerId);
    }

    /// <summary>Makes every insert of a link fail, as a failure of the database in the middle of the use would.</summary>
    private static Task FailLinkInsertsAsync(string connectionString)
    {
        return ExecuteAsync(
            connectionString,
            """
            CREATE FUNCTION fail_link_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'The link could not be written.';
            END
            $$;
            CREATE TRIGGER fail_link_insert BEFORE INSERT ON external_identities
                FOR EACH ROW EXECUTE FUNCTION fail_link_insert();
            """);
    }

    private static Task AllowLinkInsertsAsync(string connectionString)
    {
        return ExecuteAsync(connectionString, "DROP TRIGGER fail_link_insert ON external_identities; DROP FUNCTION fail_link_insert();");
    }

    private static Task<int> CountAccountsAsync(string connectionString, string email)
    {
        return CountAsync(
            connectionString,
            $"SELECT count(*)::int FROM app_users WHERE normalized_email = '{email.ToUpperInvariant()}'");
    }

    private async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "propr_pending_link_" + Guid.NewGuid().ToString("N");
        await fixture.CreateDatabaseAtMigrationAsync(name, Migration);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = name,
            Pooling = false,
        }.ConnectionString;
        return new TestDatabase(fixture.ConnectionString, name, connectionString);
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

    private static string LatestMigration()
    {
        // Listing the migrations reads the assembly and opens no connection.
        using var context = CreateContext("Host=localhost");
        return context.Database.GetMigrations().Last();
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
            await using var admin = new NpgsqlConnection(serverConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }
}
