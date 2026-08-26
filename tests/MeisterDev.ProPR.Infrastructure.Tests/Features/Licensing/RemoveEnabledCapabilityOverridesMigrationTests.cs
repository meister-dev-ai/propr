// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Infrastructure.Data;
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
///     The upgrade that clears the capability overrides carrying the enable state. Rows an earlier build wrote to
///     switch a capability on have to go, the ones that turn a capability off have to stay, and applying the
///     upgrade a second time has to leave the same result.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RemoveEnabledCapabilityOverridesMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "TrackHighestObservedTime";
    private const string PurgeMigration = "RemoveEnabledCapabilityOverrides";

    /// <summary>The number the removed enable state was stored as.</summary>
    private const int EnabledState = 1;

    private const int DisabledState = 2;

    [Fact]
    public async Task Upgrading_DeletesTheEnableOverridesAndKeepsTheDisableOverrides()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_remove_enabled_overrides_migration_{Guid.NewGuid():N}";
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
                "Skipping the capability override migration test because the configured role may not CREATE DATABASE. "
                + "Grant the role CREATEDB, or let the fixture start its own PostgreSQL container.");
            return;
        }

        try
        {
            await using (var beforeUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(beforeUpgrade, PreviousMigration);
                await SeedOverrideAsync(beforeUpgrade, PremiumCapabilityKey.MentionAnswering, EnabledState);
                await SeedOverrideAsync(beforeUpgrade, PremiumCapabilityKey.CodeInsights, EnabledState);
                await SeedOverrideAsync(beforeUpgrade, PremiumCapabilityKey.Budgeting, DisabledState);

                Assert.Equal(3, await beforeUpgrade.PremiumCapabilityOverrides.CountAsync());
            }

            await using (var afterUpgrade = CreateDbContext(scratch))
            {
                await MigrateToAsync(afterUpgrade, PurgeMigration);

                var survivor = Assert.Single(await ReadOverridesAsync(afterUpgrade));
                Assert.Equal(PremiumCapabilityKey.Budgeting, survivor.CapabilityKey);
                Assert.Equal(PremiumCapabilityOverrideState.Disabled, survivor.OverrideState);

                // Applying it again: the downgrade restores nothing, so migrating forward runs the same delete
                // over a table it has already cleared.
                await MigrateToAsync(afterUpgrade, PreviousMigration);
                await MigrateToAsync(afterUpgrade, PurgeMigration);

                var afterSecondRun = Assert.Single(await ReadOverridesAsync(afterUpgrade));
                Assert.Equal(PremiumCapabilityKey.Budgeting, afterSecondRun.CapabilityKey);
                Assert.Equal(PremiumCapabilityOverrideState.Disabled, afterSecondRun.OverrideState);
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
    ///     Writes an override row as raw SQL: the enable state is one no entity can carry any more, and the
    ///     migration exists for installations whose rows do.
    /// </summary>
    private static Task SeedOverrideAsync(MeisterProPRDbContext dbContext, string capabilityKey, int overrideState)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO premium_capability_overrides (capability_key, override_state, updated_at)
            VALUES ({0}, {1}, now());
            """,
            capabilityKey,
            overrideState);
    }

    private static async Task<List<(string CapabilityKey, PremiumCapabilityOverrideState OverrideState)>> ReadOverridesAsync(MeisterProPRDbContext dbContext)
    {
        var rows = await dbContext.PremiumCapabilityOverrides
            .AsNoTracking()
            .OrderBy(row => row.CapabilityKey)
            .Select(row => new { row.CapabilityKey, row.OverrideState })
            .ToListAsync();

        return rows.Select(row => (row.CapabilityKey, row.OverrideState)).ToList();
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
