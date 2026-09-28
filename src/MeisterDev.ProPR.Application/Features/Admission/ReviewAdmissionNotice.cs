// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Admission;

/// <summary>
///     Publishes a refused review's reason through the provider's ordinary review publication path, as a result
///     whose summary is the reason and which carries no findings. Going through that path applies the
///     per-client comment-posting setting and the generated-content marking to the notice.
/// </summary>
public sealed partial class ReviewAdmissionNotice(
    IScmProviderRegistry providerRegistry,
    IClientRegistry clientRegistry,
    ILogger<ReviewAdmissionNotice> logger) : IReviewAdmissionNotice
{
    /// <summary>The name the notice is published under where the client configured no reviewer identity.</summary>
    private const string FallbackDisplayName = "ProPR";

    /// <inheritdoc />
    public async Task PostAsync(
        ReviewJob job,
        string reason,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!await clientRegistry.GetScmCommentPostingEnabledAsync(job.ClientId, ct).ConfigureAwait(false))
        {
            return;
        }

        if (job.ReviewRevisionReference is not { } revision)
        {
            // Without a resolved revision there is nothing to anchor a thread to. The reason stays on the job.
            LogNoticeSkipped(logger, job.Id);
            return;
        }

        var identity = await this.ResolveIdentityAsync(job, ct).ConfigureAwait(false);
        var result = new ReviewResult(reason, []);

        try
        {
            await providerRegistry.GetCodeReviewPublicationService(job.Provider)
                .PublishReviewAsync(
                    job.ClientId,
                    job.CodeReviewReference,
                    revision,
                    result,
                    identity,
                    ct,
                    new ReviewPublicationContext(
                        job.CodeReviewReference,
                        revision,
                        identity,
                        existingThreads ?? [],
                        ReplyInExistingSummaryThread: true))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is the caller's decision and travels back to it. Reporting it as a posted notice
            // would tell the caller the author was informed when nothing was sent.
            throw;
        }
        catch (Exception ex)
        {
            // The job is already refused and carries the reason. A provider that refused the comment must not
            // turn the refusal into a failure, because the review was never going to run either way.
            LogNoticeFailed(logger, job.Id, ex);
        }
    }

    /// <summary>
    ///     Resolves who the notice is published as: the reviewer identity configured on the client's
    ///     connection for this host.
    /// </summary>
    /// <param name="job">The refused job.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <remarks>
    ///     A review publishes under the identity its connection authenticated as, and a provider puts that
    ///     display name in the heading of the comment it writes. A refusal is decided before the pull request
    ///     is fetched, so the authenticated identity the review reads from the pull request is not available
    ///     here, and the configured reviewer identity names the same account. Where the client configured
    ///     none, the notice publishes under the product's name and a key that identifies the connection, so
    ///     the heading stays readable.
    /// </remarks>
    private async Task<ReviewerIdentity> ResolveIdentityAsync(ReviewJob job, CancellationToken ct)
    {
        var configured = await clientRegistry
            .GetEffectiveReviewerIdentityAsync(job.ClientId, job.ProviderHost, ct)
            .ConfigureAwait(false);
        if (configured is not null)
        {
            return configured;
        }

        var connectionKey = $"connection:{job.ClientId:D}:{job.Provider}:{job.RepositoryId}:{job.PullRequestId}";
        return new ReviewerIdentity(job.ProviderHost, connectionKey, connectionKey, FallbackDisplayName, isBot: false);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Review job {JobId} was refused before it started, and no revision was resolved to post the notice against")]
    private static partial void LogNoticeSkipped(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Posting the admission refusal notice for review job {JobId} failed")]
    private static partial void LogNoticeFailed(ILogger logger, Guid jobId, Exception ex);
}
