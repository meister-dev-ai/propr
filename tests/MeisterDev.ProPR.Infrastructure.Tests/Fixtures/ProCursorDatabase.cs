// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.ProCursor.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterDev.ProPR.Infrastructure.Tests.Fixtures;

/// <summary>
///     Applies the ProCursor migrations to a test database whose ProCursor tables the ProPR migrations created. The
///     ProCursor service does the same at startup in an installation without a separate ProCursor database.
/// </summary>
internal static class ProCursorDatabase
{
    /// <summary>Applies the ProCursor migrations that the database does not have yet.</summary>
    /// <param name="connectionString">The connection string of the test database.</param>
    public static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ProCursorOperationalDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector().MigrationsHistoryTable("__EFMigrationsHistory_ProCursor"))
            // The ProCursor service ignores this warning at startup as well. EF Core cannot compare the model with the
            // ProCursor model snapshot, because the snapshot stores the status of an index job as a short value.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ProCursorOperationalDbContext(options);
        await context.Database.MigrateAsync();
    }
}
