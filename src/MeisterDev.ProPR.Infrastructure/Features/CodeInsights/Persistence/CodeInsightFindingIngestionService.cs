// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;

/// <summary>
///     Maps the findings a review increment produced onto the code-insight store. Touches the parent
///     pull-request aggregate and materialises each finding with a surrogate identifier. This is a passive
///     observer and does not affect review behaviour.
/// </summary>
/// <remarks>
///     Best-effort here rather than at the call site, like every other collection side-write. A caller that
///     produced findings has already done the work worth keeping, and containment that lives in the caller has
///     to be re-established by the next one.
/// </remarks>
public sealed partial class CodeInsightFindingIngestionService(
    ICodeInsightFindingStore store,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightFindingIngestionService> logger,
    ICodeInsightRollupProjector? rollupProjector = null) : ICodeInsightFindingIngestionService
{
    public async Task HandleReviewFindingsProducedAsync(
        ReviewFindingsProducedEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        try
        {
            await this.CollectAsync(evt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandlingFailed(logger, evt.JobId, evt.ClientId, ex);
        }
    }

    private async Task CollectAsync(ReviewFindingsProducedEvent evt, CancellationToken ct)
    {
        // Asked before the first store call, not after: an unlicensed or opted-out client must leave no row
        // behind at all, not even the pull-request aggregate.
        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return;
        }

        var key = new CodeInsightPullRequestKey(
            evt.ClientId,
            evt.RepositoryId,
            evt.PullRequestId);

        await store.TouchPullRequestAsync(key, evt.PullRequestState, evt.ObservedAt, evt.RepositoryName, ct);

        if (evt.Findings.Count == 0)
        {
            return;
        }

        var snapshots = evt.Findings
            .Select(finding => new CodeInsightFindingSnapshot(
                finding.Ordinal,
                finding.FilePath,
                finding.LineNumber,
                finding.Severity,
                finding.Message,
                finding.OriginPassKind,
                finding.OriginPassIndex,
                finding.OriginPassLens,
                finding.OriginPassShadow,
                finding.ScopeRelation,
                finding.SourceReadGrounding,
                finding.ProviderThreadId,
                finding.ProviderCommentId,
                finding.OriginModelId,
                finding.OriginLogicalModelName,
                finding.OriginSymbolName,
                finding.OriginSymbolKind))
            .ToList();

        await store.MaterialiseFindingsAsync(
            key,
            evt.JobId,
            evt.RevisionKey,
            evt.ObservedAt,
            snapshots,
            ct);

        // Keep the roll-up current. It recomputes the job's cells rather than incrementing, so calling it here
        // and again after classification or a disposition cannot double a count.
        if (rollupProjector is not null)
        {
            await rollupProjector.ProjectJobAsync(evt.JobId, ct);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Collecting the findings job {JobId} produced (client {ClientId}) failed; "
                  + "the review is unaffected.")]
    private static partial void LogHandlingFailed(ILogger logger, Guid jobId, Guid clientId, Exception ex);
}
