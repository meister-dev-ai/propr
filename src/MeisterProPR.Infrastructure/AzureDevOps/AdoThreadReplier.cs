// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     ADO implementation of <see cref="IAdoThreadReplier" />.
///     Posts a reply comment into an existing pull request thread using the ADO Git REST API.
/// </summary>
internal sealed partial class AdoThreadReplier(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoThreadReplier> logger) : IAdoThreadReplier
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <inheritdoc />
    public async Task ReplyAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string replyText,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AdoThreadReplier.Reply");
        activity?.SetTag("ado.organization_url", organizationUrl);
        activity?.SetTag("ado.pull_request_id", pullRequestId);
        activity?.SetTag("ado.thread_id", threadId);

        var credentials = clientId.HasValue
            ? await credentialRepository.GetByClientIdAsync(clientId.Value, cancellationToken)
            : null;

        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken: cancellationToken);
        var gitClient = connection.GetClient<GitHttpClient>();

        var comment = new Comment
        {
            Content = replyText,
            CommentType = CommentType.Text,
        };

        await gitClient.CreateCommentAsync(
            comment,
            repositoryId,
            pullRequestId,
            threadId,
            projectId,
            cancellationToken);

        LogReplied(logger, organizationUrl, pullRequestId, threadId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AdoThreadReplier: posted reply to {OrganizationUrl} PR#{PullRequestId} thread {ThreadId}")]
    private static partial void LogReplied(ILogger logger, string organizationUrl, int pullRequestId, int threadId);
}
