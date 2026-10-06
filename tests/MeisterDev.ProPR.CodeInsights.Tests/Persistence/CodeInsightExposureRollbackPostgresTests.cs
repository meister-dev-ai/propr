// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

[Collection("PostgresIntegration")]
public sealed class CodeInsightExposureRollbackPostgresTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task ExposureRollbackPreservesWideIdentitiesAndUniquenessAcrossReupgrade()
    {
        fixture.SkipIfUnavailable();
        const string identityMigration = "20261003130957_BoundCodeInsightExposureIdentity";
        const string precedingMigration = "20261003031757_OrderReviewerPerformanceMissObservations";
        var databaseName = $"propr_exposure_rollback_{Guid.NewGuid():N}";
        await fixture.CreateDatabaseAtMigrationAsync(databaseName, identityMigration);
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName, Pooling = false };
        try
        {
            await using var db = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>()
                    .UseNpgsql(builder.ConnectionString, options => options.UseVector())
                    .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
            var now = DateTimeOffset.UtcNow;
            var tenant = Guid.NewGuid();
            var client = Guid.NewGuid();
            var pr = Guid.NewGuid();
            var job = Guid.NewGuid();
            var exposureId = Guid.NewGuid();
            var random = new Random(173);
            var path = string.Concat(
                Enumerable.Range(0, 512).Select(index =>
                    index % 50 == 49 ? "/" : char.ConvertFromUtf32(random.Next(0x20000, 0x2FFFF))));
            var model = string.Concat(Enumerable.Range(0, 256).Select(_ => (char)random.Next(0x1000, 0xD700)));
            var logical = string.Concat(Enumerable.Range(0, 128).Select(_ => (char)random.Next('a', 'z' + 1)));
            const string source = "completed-file-baseline";
            var changedModel = model[..^1] + 'a';
            db.AddRange(
                new TenantRecord { Id = tenant, Slug = tenant.ToString("N"), DisplayName = "Exposure rollback", IsActive = true, CreatedAt = now },
                new ClientRecord { Id = client, TenantId = tenant, DisplayName = "Exposure rollback", IsActive = true, CreatedAt = now },
                new CodeInsightPullRequest { Id = pr, ClientId = client, RepositoryId = "rollback", PullRequestId = 1, CreatedAt = now });
            await db.SaveChangesAsync();

            Task<int> InsertAsync(Guid id, string modelId) => db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO code_insight_review_exposures
                 ("Id", "CodeInsightPullRequestId", "JobId", "FilePath", "RevisionKey", "ModelId", "LogicalModelName", "ProviderScope", "Source", "ObservedAt")
                 VALUES ({id}, {pr}, {job}, {path}, 'head', {modelId}, {logical}, 'GitHub:https://example.test', {source}, {now})
                 """);

            async Task AssertOriginalIdentityAsync()
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    SELECT "JobId", "FilePath", "ModelId", "LogicalModelName", "Source"
                    FROM code_insight_review_exposures WHERE "Id" = @id
                    """, connection);
                command.Parameters.AddWithValue("id", exposureId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(job, reader.GetGuid(0));
                Assert.Equal(path, reader.GetString(1));
                Assert.Equal(model, reader.GetString(2));
                Assert.Equal(logical, reader.GetString(3));
                Assert.Equal(source, reader.GetString(4));
                Assert.False(await reader.ReadAsync());
            }

            await InsertAsync(exposureId, model);
            await AssertOriginalIdentityAsync();
            await db.GetService<IMigrator>().MigrateAsync(precedingMigration);
            await AssertOriginalIdentityAsync();
            var duplicateAfterRollback = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(Guid.NewGuid(), model));
            Assert.Equal("23505", duplicateAfterRollback.SqlState);
            await InsertAsync(Guid.NewGuid(), changedModel);

            await db.GetService<IMigrator>().MigrateAsync(identityMigration);
            await AssertOriginalIdentityAsync();
            var rows = await db.CodeInsightReviewExposures.AsNoTracking().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal(64, row.IdentityFingerprint.Length));
            Assert.Equal(2, rows.Select(row => row.IdentityFingerprint).Distinct().Count());
            Assert.Contains(rows, row => row.ModelId == changedModel && row.FilePath == path && row.LogicalModelName == logical);
            var duplicateAfterReupgrade = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(Guid.NewGuid(), model));
            Assert.Equal("23505", duplicateAfterReupgrade.SqlState);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
