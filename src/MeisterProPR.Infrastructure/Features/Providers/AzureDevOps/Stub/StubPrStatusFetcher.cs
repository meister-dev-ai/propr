// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IPrStatusFetcher" /> for dev/stub mode.
///     Always returns <see cref="PrStatus.Active" /> so that no jobs are incorrectly cancelled
///     when ADO is not connected.
/// </summary>
public sealed class StubPrStatusFetcher : IPrStatusFetcher
{
    /// <inheritdoc />
    public Task<PrStatus> GetStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId,
        CancellationToken ct = default)
    {
        return Task.FromResult(PrStatus.Active);
    }
}
