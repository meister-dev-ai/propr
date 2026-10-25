// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>Reads PR summaries from offline review execution without a database.</summary>
public sealed class InMemoryPrReviewViewReader(InMemoryReviewJobRepository repository) : IPrReviewViewReader
{
    /// <inheritdoc />
    public async Task<PrReviewViewData> ReadAsync(
        Guid clientId, string providerScopePath, string providerProjectKey, string repositoryId,
        int pullRequestId, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var jobs = await repository.GetByPrAsync(
            clientId, providerScopePath, providerProjectKey,
            repositoryId, pullRequestId, page, pageSize, ct).ConfigureAwait(false);
        var summaries = jobs.Select(job => new PrJobSummaryDto(
            job.Id, job.Status, job.SubmittedAt, job.CompletedAt, job.Result?.Comments.Count,
            job.TotalInputTokensAggregated ?? job.Protocols.Sum(protocol => protocol.TotalInputTokens),
            job.TotalOutputTokensAggregated ?? job.Protocols.Sum(protocol => protocol.TotalOutputTokens),
            job.TokenBreakdown, job.TotalEstimatedCostUsd, job.CostIsApproximate)).ToList();
        var samples = jobs.SelectMany(job => job.Protocols)
            .SelectMany(protocol => protocol.Events)
            .Where(ev => ev.Name == ReviewProtocolEventNames.MemoryReconsiderationCompleted && ev.InputTextSample != null)
            .Select(ev => ev.InputTextSample!).ToList();

        return new PrReviewViewData(summaries, samples, [], null);
    }
}
