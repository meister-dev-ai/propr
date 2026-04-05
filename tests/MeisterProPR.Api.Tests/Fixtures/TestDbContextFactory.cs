// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
