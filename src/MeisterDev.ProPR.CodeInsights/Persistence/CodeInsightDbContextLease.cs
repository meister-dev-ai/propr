// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Owns a factory context while preserving the caller's fallback context.</summary>
internal sealed class CodeInsightDbContextLease(MeisterProPRDbContext context, bool ownsContext) : IAsyncDisposable
{
    internal MeisterProPRDbContext Context { get; } = context;

    internal static async Task<CodeInsightDbContextLease> CreateAsync(
        MeisterProPRDbContext fallback, IDbContextFactory<MeisterProPRDbContext>? factory, CancellationToken ct)
    {
        return factory is null ? new(fallback, false) : new(await factory.CreateDbContextAsync(ct), true);
    }

    public async ValueTask DisposeAsync()
    {
        if (ownsContext)
        {
            await this.Context.DisposeAsync();
        }
    }
}
