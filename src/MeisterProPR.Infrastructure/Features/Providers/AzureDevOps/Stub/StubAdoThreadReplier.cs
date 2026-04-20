// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IReviewThreadReplyPublisher" /> used when <c>ADO_STUB_PR=true</c>.
///     Logs the reply text instead of posting to ADO.
/// </summary>
internal sealed partial class StubAdoThreadReplier(ILogger<StubAdoThreadReplier> logger) : IReviewThreadReplyPublisher
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public Task ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReplyAsync(
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
        return Task.CompletedTask;
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
