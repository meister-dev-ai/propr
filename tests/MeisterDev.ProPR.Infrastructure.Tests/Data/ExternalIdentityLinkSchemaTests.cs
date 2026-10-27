// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Data;

/// <summary>
///     Checks the schema of <c>external_identities</c> after the migration that lets a link exist without a tenant.
///     A link made through a tenant identity provider names the tenant and the provider. A link made through the
///     identity provider for platform administrators names neither.
/// </summary>
[Collection("PostgresIntegration3")]
public sealed class ExternalIdentityLinkSchemaTests(PostgresContainerFixture fixture)
{
    private const string Issuer = "https://id.example.test/realms/staff";

    [Fact]
    public async Task UpgradeKeepsTenantLinksAndAcceptsOneLinkWithoutTenantPerExternalSubjectAndDowngradeRemovesThose()
    {
        fixture.SkipIfUnavailable();
        var database = "propr_external_identity_" + Guid.NewGuid().ToString("N");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = database,
            Pooling = false,
        }.ConnectionString;
        var tenantId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var tenantUserId = Guid.NewGuid();
        var tenantLinkId = Guid.NewGuid();
        var administratorId = Guid.NewGuid();
        try
        {
            await fixture.CreateDatabaseAtMigrationAsync(database, "AddClientWorkStoppedAt");
            await using (var before = CreateContext(connectionString))
            {
                var now = DateTimeOffset.UtcNow;
                await before.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
                     VALUES ({tenantId}, {tenantId.ToString("N")}, 'Acme', true, {now}, {now});
                     INSERT INTO tenant_sso_providers
                       (id, tenant_id, display_name, provider_kind, protocol_kind, client_id, scopes, allowed_email_domains,
                        is_enabled, auto_create_users, created_at, updated_at)
                     VALUES ({providerId}, {tenantId}, 'Acme Entra', 'EntraId', 'Oidc', 'client', '[]'::jsonb, '[]'::jsonb,
                       true, true, {now}, {now});
                     INSERT INTO app_users (id, username, global_role, is_active, created_at)
                     VALUES ({tenantUserId}, {"tenant-" + tenantUserId.ToString("N")}, 'User', true, {now});
                     INSERT INTO app_users (id, username, global_role, is_active, created_at)
                     VALUES ({administratorId}, {"admin-" + administratorId.ToString("N")}, 'Admin', true, {now});
                     INSERT INTO external_identities
                       (id, tenant_id, user_id, sso_provider_id, issuer, subject, email, email_verified, created_at)
                     VALUES ({tenantLinkId}, {tenantId}, {tenantUserId}, {providerId}, {Issuer}, 'subject-1',
                       'user@acme.test', true, {now})
                     """);
            }

            await using var after = CreateContext(connectionString);
            await after.Database.MigrateAsync();
            Assert.False(after.Database.HasPendingModelChanges());

            var tenantLink = await after.ExternalIdentities.AsNoTracking().SingleAsync(link => link.Id == tenantLinkId);
            Assert.Equal(tenantId, tenantLink.TenantId);
            Assert.Equal(providerId, tenantLink.SsoProviderId);

            await InsertLinkAsync(after, null, administratorId, null, "subject-1");

            var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertLinkAsync(after, null, administratorId, null, "subject-1"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

            var tenantWithoutProvider = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertLinkAsync(after, tenantId, administratorId, null, "subject-2"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, tenantWithoutProvider.SqlState);

            var providerWithoutTenant = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertLinkAsync(after, null, administratorId, providerId, "subject-3"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, providerWithoutTenant.SqlState);

            // The downgrade removes the links without a tenant and restores both columns as required, without default.
            await after.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync("AddClientWorkStoppedAt");
            Assert.Equal(1, await CountAsync(after, "SELECT count(*)::int AS \"Value\" FROM external_identities"));
            Assert.Equal(
                2,
                await CountAsync(
                    after,
                    """
                    SELECT count(*)::int AS "Value" FROM information_schema.columns
                    WHERE table_name = 'external_identities' AND column_name IN ('tenant_id', 'sso_provider_id')
                      AND is_nullable = 'NO' AND column_default IS NULL
                    """));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static Task<int> InsertLinkAsync(
        MeisterProPRDbContext context,
        Guid? tenantId,
        Guid userId,
        Guid? providerId,
        string subject)
    {
        var now = DateTimeOffset.UtcNow;
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO external_identities
               (id, tenant_id, user_id, sso_provider_id, issuer, subject, email, email_verified, created_at)
             VALUES ({Guid.NewGuid()}, {tenantId}, {userId}, {providerId}, {Issuer}, {subject}, 'operator@example.test',
               true, {now})
             """);
    }

    private static Task<int> CountAsync(MeisterProPRDbContext context, string sql)
    {
        return context.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }

    private static MeisterProPRDbContext CreateContext(string connectionString)
    {
        return new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
                .Options);
    }
}
