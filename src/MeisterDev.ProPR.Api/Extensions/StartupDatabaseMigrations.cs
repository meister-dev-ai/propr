// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MeisterDev.ProPR.Api.Extensions;

/// <summary>
///     Brings the database schema up to date at startup and reports what it did at the level an operator runs
///     the installation at.
/// </summary>
public static class StartupDatabaseMigrations
{
    /// <summary>Applies every pending migration and reports each one.</summary>
    /// <param name="dbContext">The context whose database is migrated.</param>
    /// <param name="logger">Where the step reports what it applied.</param>
    /// <param name="ct">The cancellation token.</param>
    public static async Task ApplyAsync(MeisterProPRDbContext dbContext, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Create the history table before querying it so an empty database does not log an expected
        // missing-table command failure. Startup migration still uses EF Core's MigrateAsync.
        await dbContext.Database.GetService<IHistoryRepository>().CreateIfNotExistsAsync(ct);

        var applied = await dbContext.Database.GetAppliedMigrationsAsync(ct);
        var pending = await dbContext.Database.GetPendingMigrationsAsync(ct);

        Report(logger, [.. pending], applied.Count());

        await dbContext.Database.MigrateAsync(ct);
    }

    /// <summary>Writes one line per migration that is about to be applied, or one line when none are.</summary>
    /// <param name="logger">Where the lines are written.</param>
    /// <param name="pendingMigrations">The migrations that are about to be applied, in the order they run.</param>
    /// <param name="appliedMigrationCount">How many migrations the database already carries.</param>
    /// <remarks>
    ///     Application-category information logs retain migration visibility when Microsoft categories
    ///     are suppressed by the installation's logging configuration.
    /// </remarks>
    public static void Report(ILogger logger, IReadOnlyList<string> pendingMigrations, int appliedMigrationCount)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pendingMigrations);

        if (pendingMigrations.Count == 0)
        {
            logger.LogInformation(
                "The database schema is up to date: {AppliedMigrationCount} migrations are applied and none are pending.",
                appliedMigrationCount);
            return;
        }

        foreach (var migrationId in pendingMigrations)
        {
            logger.LogInformation("Applying database migration {MigrationId}.", migrationId);
        }
    }
}
