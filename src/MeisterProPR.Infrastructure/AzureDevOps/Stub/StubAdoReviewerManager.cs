// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>No-op implementation of <see cref="IAdoReviewerManager" /> for dev/stub mode.</summary>
public sealed class StubAdoReviewerManager : IAdoReviewerManager
{
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
