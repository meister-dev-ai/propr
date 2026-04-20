// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MeisterProPR.Infrastructure.Data;

/// <summary>
///     Design-time factory for <see cref="MeisterProPRDbContext" />. Used by EF Core tooling
///     (dotnet ef migrations) when no application service provider is available.
/// </summary>
public sealed class MeisterProPRDbContextFactory : IDesignTimeDbContextFactory<MeisterProPRDbContext>
{
    /// <inheritdoc />
    public MeisterProPRDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
                               ?? "Host=localhost;Database=meisterpropr;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
