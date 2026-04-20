// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed provider for per-client review settings.</summary>
public sealed class DbClientRegistry(
    MeisterProPRDbContext dbContext,
    IClientScmConnectionRepository connectionRepository,
    IClientReviewerIdentityRepository reviewerIdentityRepository) : IClientRegistry
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
    public async Task<ReviewerIdentity?> GetReviewerIdentityAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null)
        {
            return null;
        }

        var identity = await reviewerIdentityRepository.GetByConnectionIdAsync(clientId, connection.Id, ct);
        if (identity is null)
        {
            return null;
        }

        return new ReviewerIdentity(
            host,
            identity.ExternalUserId,
            identity.Login,
            identity.DisplayName,
            identity.IsBot);
    }

    /// <inheritdoc />
    public async Task<CommentResolutionBehavior> GetCommentResolutionBehaviorAsync(
        Guid clientId,
        CancellationToken ct = default)
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
