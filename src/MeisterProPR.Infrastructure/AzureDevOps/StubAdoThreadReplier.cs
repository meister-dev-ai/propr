using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     No-op implementation of <see cref="IAdoThreadReplier" /> used when <c>ADO_STUB_PR=true</c>.
///     Logs the reply text instead of posting to ADO.
/// </summary>
internal sealed partial class StubAdoThreadReplier(ILogger<StubAdoThreadReplier> logger) : IAdoThreadReplier
{
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
        Message = "StubAdoThreadReplier: would reply to {OrganizationUrl}/{ProjectId}/{RepositoryId} PR#{PullRequestId} thread {ThreadId}: {ReplyText}")]
    private static partial void LogStubReply(
        ILogger logger,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string replyText);
}
