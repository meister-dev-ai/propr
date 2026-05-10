// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MeisterProPR.ProCursor.Persistence;

/// <summary>
///     Design-time factory for <see cref="ProCursorOperationalDbContext" />.
/// </summary>
public sealed class ProCursorOperationalDbContextFactory : IDesignTimeDbContextFactory<ProCursorOperationalDbContext>
{
    /// <inheritdoc />
    public ProCursorOperationalDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PROCURSOR_DB_CONNECTION_STRING")
                               ?? "Host=localhost;Database=meisterpropr;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<ProCursorOperationalDbContext>()
            .UseNpgsql(
                connectionString,
                o => o.UseVector().MigrationsHistoryTable("__EFMigrationsHistory_ProCursor"))
            .Options;

        return new ProCursorOperationalDbContext(options);
    }
}
