using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Api.Tests.Fixtures;

/// <summary>
///     Minimal <see cref="IDbContextFactory{TContext}" /> implementation for integration tests.
///     Creates a new <see cref="MeisterProPRDbContext" /> from the supplied options on every call.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<MeisterProPRDbContext> options)
    : IDbContextFactory<MeisterProPRDbContext>
{
    public MeisterProPRDbContext CreateDbContext() => new(options);
}
