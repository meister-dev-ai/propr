// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed provider for per-client review settings.</summary>
public sealed class DbClientRegistry(
    MeisterProPRDbContext dbContext,
    IClientScmConnectionRepository connectionRepository,
    IClientReviewerIdentityRepository reviewerIdentityRepository,
    Func<ProviderHostRef, ClientScmConnectionCredentialDto, CancellationToken, Task<ReviewerIdentity?>>?
        deriveReviewerIdentityAsync = null,
    ILogger<DbClientRegistry>? logger = null) : IClientRegistry
{
    private readonly Func<ProviderHostRef, ClientScmConnectionCredentialDto, CancellationToken, Task<ReviewerIdentity?>>?
        _deriveReviewerIdentityAsync = deriveReviewerIdentityAsync;

    private readonly ILogger<DbClientRegistry> _logger = logger ?? NullLogger<DbClientRegistry>.Instance;

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
    public async Task<ReviewerIdentity?> GetEffectiveReviewerIdentityAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        var configuredIdentity = await this.GetReviewerIdentityAsync(clientId, host, ct);
        if (configuredIdentity is not null)
        {
            return configuredIdentity;
        }

        if (host.Provider != ScmProvider.GitHub || this._deriveReviewerIdentityAsync is null)
        {
            return null;
        }

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null || connection.AuthenticationKind != ScmAuthenticationKind.AppInstallation)
        {
            return null;
        }

        try
        {
            return await this._deriveReviewerIdentityAsync(host, connection, ct);
        }
        catch (Exception ex)
        {
            var safeHostBaseUrl = host.HostBaseUrl.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            this._logger.LogWarning(
                ex,
                "Failed to derive GitHub App reviewer identity for client {ClientId} on host {HostBaseUrl}; continuing without fallback reviewer identity.",
                clientId,
                safeHostBaseUrl);
            return null;
        }
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

    /// <inheritdoc />
    public async Task<bool> GetScmCommentPostingEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
                   .Where(c => c.Id == clientId)
                   .Select(c => (bool?)c.ScmCommentPostingEnabled)
                   .FirstOrDefaultAsync(ct)
               ?? true;
    }

    /// <inheritdoc />
    public async Task<bool> GetEvidenceBackedVerificationEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
                   .Where(c => c.Id == clientId)
                   .Select(c => (bool?)c.EnableEvidenceBackedVerification)
                   .FirstOrDefaultAsync(ct)
               ?? false;
    }

    /// <inheritdoc />
    public async Task<bool> GetLanguageRobustScreeningEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
                   .Where(c => c.Id == clientId)
                   .Select(c => (bool?)c.EnableLanguageRobustScreening)
                   .FirstOrDefaultAsync(ct)
               ?? false;
    }

    /// <inheritdoc />
    public async Task<bool> GetMultiPassUnionEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
                   .Where(c => c.Id == clientId)
                   .Select(c => (bool?)c.EnableMultiPassUnion)
                   .FirstOrDefaultAsync(ct)
               ?? false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewPassSpec>> GetReviewPassesAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.ClientReviewPasses
            .Where(pass => pass.ClientId == clientId)
            .OrderBy(pass => pass.Ordinal)
            .Select(pass => new ReviewPassSpec(pass.ConfiguredModelId, pass.Lens))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ReviewStrategy?> GetDefaultReviewStrategyAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => (ReviewStrategy?)c.DefaultReviewStrategy)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetDefaultReviewPipelineProfileIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.DefaultReviewPipelineProfileId)
            .FirstOrDefaultAsync(ct);
    }
}
