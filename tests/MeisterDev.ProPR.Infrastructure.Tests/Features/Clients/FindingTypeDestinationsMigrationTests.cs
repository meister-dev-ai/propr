// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Clients;

/// <summary>
///     The upgrade that replaces the list of finding types that are not posted with a destination per finding type. A
///     client with cleared types has to read back the same types, through the earlier field and as reviewOnly entries,
///     every client has to receive the default for requests for missing tests and documentation unless it cleared that
///     type, an existing client has to receive the default limit on posted findings that a later upgrade adds, and the
///     downgrade has to restore the list.
/// </summary>
[Collection("PostgresIntegration4")]
public sealed class FindingTypeDestinationsMigrationTests(PostgresContainerFixture fixture)
{
    private const string PreviousMigration = "AddClientRiskAndFindingTypePublicationFilters";

    [Fact]
    public async Task Upgrading_TurnsClearedTypesIntoReviewOnlyEntriesAndAddsTheDefault()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_finding_type_destinations_{Guid.NewGuid():N}";
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName, Pooling = false }
            .ConnectionString;

        try
        {
            await fixture.CreateDatabaseAtMigrationAsync(databaseName, PreviousMigration);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // The scratch database keeps the historical schema away from the shared one. A server reached through
            // DB_CONNECTION_STRING may not grant the role CREATEDB, which is a missing privilege of the test setup.
            Skip.If(true, "Skipping the finding type destinations migration test because the configured role may not CREATE DATABASE.");
            return;
        }

        try
        {
            await using var db = CreateDbContext(scratch);
            var tenantId = Guid.NewGuid();
            var clearedTwo = Guid.NewGuid();
            var clearedDocumentation = Guid.NewGuid();
            var clearedNothing = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
                 VALUES ({tenantId}, {databaseName}, 'Finding type destinations', true, {now}, {now});
                 INSERT INTO clients (id, tenant_id, display_name, is_active, created_at, withheld_finding_types)
                 VALUES ({clearedTwo}, {tenantId}, 'Two cleared types', true, {now}, 'naming-clarity,security'),
                        ({clearedDocumentation}, {tenantId}, 'Documentation cleared', true, {now}, 'documentation-tests'),
                        ({clearedNothing}, {tenantId}, 'Nothing cleared', true, {now}, '');
                 """);

            // The service reads a client through the current model, so the database is brought to the latest migration,
            // which includes this upgrade and every later one.
            await db.Database.MigrateAsync();

            var service = new ClientAdminService(db, new UnlimitedStockQuotaGate());

            var two = (await service.GetByIdAsync(clearedTwo))!;
            Assert.Equal(["security", "naming-clarity"], two.WithheldFindingTypesOrEmpty);
            Assert.Equal(20, two.MaximumFindingsToPost);
            Assert.Equal(
                new Dictionary<string, FindingPostDestination>
                {
                    ["documentation-tests:missing"] = FindingPostDestination.Summary,
                    ["naming-clarity"] = FindingPostDestination.ReviewOnly,
                    ["security"] = FindingPostDestination.ReviewOnly,
                },
                two.FindingTypeDestinationsOrDefault);

            // A client that kept every documentation and test finding off the pull request does not get the requests for
            // missing tests listed in the summary, because the key with the qualifier would take precedence.
            var documentation = (await service.GetByIdAsync(clearedDocumentation))!;
            Assert.Equal(["documentation-tests"], documentation.WithheldFindingTypesOrEmpty);
            Assert.Equal(
                new Dictionary<string, FindingPostDestination> { ["documentation-tests"] = FindingPostDestination.ReviewOnly },
                documentation.FindingTypeDestinationsOrDefault);

            var nothing = (await service.GetByIdAsync(clearedNothing))!;
            Assert.Empty(nothing.WithheldFindingTypesOrEmpty);
            Assert.Equal(
                new Dictionary<string, FindingPostDestination> { ["documentation-tests:missing"] = FindingPostDestination.Summary },
                nothing.FindingTypeDestinationsOrDefault);

            await MigrateToAsync(db, PreviousMigration);

            Assert.Equal("naming-clarity,security", await ReadWithheldTypesAsync(scratch, clearedTwo));
            Assert.Equal("documentation-tests", await ReadWithheldTypesAsync(scratch, clearedDocumentation));
            Assert.Equal(string.Empty, await ReadWithheldTypesAsync(scratch, clearedNothing));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string?> ReadWithheldTypesAsync(string connectionString, Guid clientId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT withheld_finding_types FROM clients WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", clientId);
        return (string?)await command.ExecuteScalarAsync();
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
}
