// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     The upgrade that clears a stored commercial edition declaration. An installation that declared the
///     commercial edition has to come back to community with its activation stamp gone, an installation that never
///     declared one has to be left as it is, and applying the upgrade a second time has to leave the same result.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ClearDeclaredCommercialEditionMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "RemoveEnabledCapabilityOverrides";
    private const string ClearMigration = "ClearDeclaredCommercialEdition";

    /// <summary>The single row the installation's licensing policy is stored as.</summary>
    private const int SingletonPolicyId = 1;

    /// <summary>The number the declared commercial edition was stored as.</summary>
    private const int DeclaredCommercial = 1;

    private const int Community = 0;

    private static readonly DateTimeOffset PolicyUpdatedAt = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PolicyUpdatedBy = new("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset DeclaredAt = new(2026, 5, 2, 8, 30, 0, TimeSpan.Zero);
    private static readonly Guid DeclaredBy = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Upgrading_ClearsADeclaredCommercialEditionAndLeavesACommunityInstallationAsItIs()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_clear_declared_commercial_migration_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        try
        {
            await ExecuteOnServerAsync(adminConnectionString, $"CREATE DATABASE \"{databaseName}\";");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // The scratch database keeps the historical schema away from the shared one. A server reached through
            // DB_CONNECTION_STRING may not grant the role CREATEDB, and that is a missing privilege rather than a
            // failure of the migration under test.
            Skip.If(
                true,
                "Skipping the declared edition migration test because the configured role may not CREATE DATABASE. "
                + "Grant the role CREATEDB, or let the fixture start its own PostgreSQL container.");
            return;
        }

        try
        {
            await using (var communityInstallation = CreateDbContext(scratch))
            {
                await MigrateToAsync(communityInstallation, PreviousMigration);
                await SeedCommunityInstallationAsync(communityInstallation);

                await MigrateToAsync(communityInstallation, ClearMigration);

                AssertCommunityWithoutActivation(await ReadEditionAsync(communityInstallation));
            }

            await using (var declaredInstallation = CreateDbContext(scratch))
            {
                await DeclareCommercialEditionAsync(declaredInstallation);

                // The downgrade restores nothing, so migrating back and forward runs the upgrade over the row as
                // an installation that declared the commercial edition would carry it.
                await MigrateToAsync(declaredInstallation, PreviousMigration);
                await MigrateToAsync(declaredInstallation, ClearMigration);

                AssertCommunityWithoutActivation(await ReadEditionAsync(declaredInstallation));

                // Applying it again, over the row it has already cleared.
                await MigrateToAsync(declaredInstallation, PreviousMigration);
                await MigrateToAsync(declaredInstallation, ClearMigration);

                AssertCommunityWithoutActivation(await ReadEditionAsync(declaredInstallation));
            }
        }
        finally
        {
            await ExecuteOnServerAsync(
                adminConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
        }
    }

    /// <summary>
    ///     What the row has to read as after the upgrade, whichever state it went into it with. The row carries
    ///     more than the edition: who last changed the installation's capability policy is read elsewhere, so the
    ///     upgrade has to leave it as it was.
    /// </summary>
    private static void AssertCommunityWithoutActivation(InstallationEditionRecord record)
    {
        Assert.Equal(InstallationEdition.Community, record.Edition);
        Assert.Null(record.ActivatedAt);
        Assert.Null(record.ActivatedByUserId);
        Assert.Equal(PolicyUpdatedAt, record.UpdatedAt);
        Assert.Equal(PolicyUpdatedBy, record.UpdatedByUserId);
    }

    /// <summary>Writes the singleton policy row as an installation that never declared an edition carries it.</summary>
    private static Task SeedCommunityInstallationAsync(MeisterProPRDbContext dbContext)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO installation_edition
                (id, edition, activated_at, activated_by_user_id, updated_at, updated_by_user_id)
            VALUES ({0}, {1}, NULL, NULL, {2}, {3});
            """,
            SingletonPolicyId,
            Community,
            PolicyUpdatedAt,
            PolicyUpdatedBy);
    }

    /// <summary>
    ///     Puts the row into the state the removed write path left behind: the declared commercial edition, with
    ///     the instant and the administrator it was declared by. No write path produces it any more, so the test
    ///     writes it as raw SQL.
    /// </summary>
    private static Task DeclareCommercialEditionAsync(MeisterProPRDbContext dbContext)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE installation_edition
            SET edition = {0},
                activated_at = {1},
                activated_by_user_id = {2}
            WHERE id = {3};
            """,
            DeclaredCommercial,
            DeclaredAt,
            DeclaredBy,
            SingletonPolicyId);
    }

    private static Task<InstallationEditionRecord> ReadEditionAsync(MeisterProPRDbContext dbContext)
    {
        return dbContext.InstallationEditions
            .AsNoTracking()
            .SingleAsync(record => record.Id == SingletonPolicyId);
    }

    private static MeisterProPRDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static Task MigrateToAsync(MeisterProPRDbContext dbContext, string targetMigration)
    {
        return dbContext.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task ExecuteOnServerAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
