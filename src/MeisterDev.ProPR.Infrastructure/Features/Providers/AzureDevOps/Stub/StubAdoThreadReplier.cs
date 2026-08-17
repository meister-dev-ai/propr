// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IReviewThreadReplyPublisher" /> used when <c>ADO_STUB_PR=true</c>.
///     Logs the reply text instead of posting to ADO.
/// </summary>
internal sealed partial class StubAdoThreadReplier(ILogger<StubAdoThreadReplier> logger) : IReviewThreadReplyPublisher
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    // Nothing is posted, so there is no provider comment to report and provenance recording is skipped.
    public Task<string?> ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default,
        string? quotedComment = null)
    {
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc cref="ReplyAsync(Guid, ReviewThreadRef, string, CancellationToken, string)" />
    public Task<string?> ReplyAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string replyText,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        LogStubReply(logger, organizationUrl, projectId, repositoryId, pullRequestId, threadId, replyText);
        return Task.FromResult<string?>(null);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "StubAdoThreadReplier: would reply to {OrganizationUrl}/{ProjectId}/{RepositoryId} PR#{PullRequestId} thread {ThreadId}: {ReplyText}")]
    private static partial void LogStubReply(
        ILogger logger,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string replyText);
}
