// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.Fixtures;

/// <summary>
///     Minimal <see cref="IDbContextFactory{TContext}" /> implementation for unit/integration tests.
///     Creates a new <see cref="MeisterProPRDbContext" /> from the supplied options on every call.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<MeisterProPRDbContext> options)
    : IDbContextFactory<MeisterProPRDbContext>
{
    public MeisterProPRDbContext CreateDbContext()
    {
        return new MeisterProPRDbContext(options);
    }
}
