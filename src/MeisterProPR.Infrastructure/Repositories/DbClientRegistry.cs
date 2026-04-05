// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed provider for per-client review settings.</summary>
public sealed class DbClientRegistry(MeisterProPRDbContext dbContext) : IClientRegistry
{
    /// <inheritdoc />
    public async Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.ReviewerId)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<CommentResolutionBehavior> GetCommentResolutionBehaviorAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.CommentResolutionBehavior)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetCustomSystemMessageAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.CustomSystemMessage)
            .FirstOrDefaultAsync(ct);
    }
}
