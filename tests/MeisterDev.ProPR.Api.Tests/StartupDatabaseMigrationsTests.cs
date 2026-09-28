// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Tests.Fixtures;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Api.Tests;

/// <summary>
///     What an operator reads when the installation starts: one line per migration the start applies, and no
///     error for a command that is expected to fail.
/// </summary>
[Collection("PostgresApiIntegration")]
public sealed class StartupDatabaseMigrationsTests(PostgresContainerFixture fixture)
{
    [Fact]
    public void Report_WithPendingMigrations_WritesOneInformationLinePerMigration()
    {
        var logger = new RecordingLogger();

        StartupDatabaseMigrations.Report(logger, ["20260101000000_First", "20260102000000_Second"], appliedMigrationCount: 3);

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.Contains("20260101000000_First", logger.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("20260102000000_Second", logger.Entries[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_WithoutPendingMigrations_WritesOneLineCarryingTheAppliedCount()
    {
        var logger = new RecordingLogger();

        StartupDatabaseMigrations.Report(logger, [], appliedMigrationCount: 137);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("137", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The first start of an installation runs against a database with no history table. Reading it is
    ///     part of how EF Core finds that out, and an operator reading the log must not be told that the start
    ///     failed at something.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_AgainstAnEmptyDatabase_MigratesItWithoutReportingAFailedCommand()
    {
        fixture.SkipIfUnavailable();

        var databaseName = $"propr_startup_migrations_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString).ConnectionString;
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }
            .ConnectionString;

        await ExecuteOnServerAsync(admin, $"CREATE DATABASE \"{databaseName}\";");

        try
        {
            var failures = new List<string>();
            var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(scratch, o => o.UseVector())
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .LogTo(failures.Add, [RelationalEventId.CommandError])
                .Options;

            var logger = new RecordingLogger();
            await using (var dbContext = new MeisterProPRDbContext(options))
            {
                await StartupDatabaseMigrations.ApplyAsync(dbContext, logger);
            }

            Assert.Empty(failures);
            Assert.NotEmpty(logger.Entries);

            // The schema is there, so a second start has nothing to apply and says so in one line.
            var second = new RecordingLogger();
            await using (var dbContext = new MeisterProPRDbContext(options))
            {
                await StartupDatabaseMigrations.ApplyAsync(dbContext, second);
            }

            Assert.Empty(failures);
            Assert.Single(second.Entries);
        }
        finally
        {
            await ExecuteOnServerAsync(admin, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
        }
    }

    private static async Task ExecuteOnServerAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Keeps every line written to it, so a test can read what an operator would.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullLogger.Instance.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            this.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
