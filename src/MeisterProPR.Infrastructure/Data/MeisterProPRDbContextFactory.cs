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
            .UseNpgsql(connectionString)
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
