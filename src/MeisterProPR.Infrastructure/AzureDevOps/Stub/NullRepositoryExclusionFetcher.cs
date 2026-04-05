// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IRepositoryExclusionFetcher" /> used when
///     <c>ADO_STUB_PR=true</c> is set. Always returns <see cref="ReviewExclusionRules.Default" />.
/// </summary>
internal sealed class NullRepositoryExclusionFetcher : IRepositoryExclusionFetcher
{
    /// <inheritdoc />
    public Task<ReviewExclusionRules> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ReviewExclusionRules.Default);
    }
}
