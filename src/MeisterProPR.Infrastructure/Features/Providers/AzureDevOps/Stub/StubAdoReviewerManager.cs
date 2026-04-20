// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

/// <summary>No-op implementation of <see cref="IReviewAssignmentService" /> for dev/stub mode.</summary>
public sealed class StubAdoReviewerManager : IReviewAssignmentService
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public Task AddOptionalReviewerAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewerIdentity reviewer,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddOptionalReviewerAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
