// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>
///     Azure DevOps implementation of <see cref="IReviewThreadReplyPublisher" />.
///     Posts a reply comment into an existing pull request thread using the ADO Git REST API.
/// </summary>
internal sealed partial class AdoThreadReplier(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    ILogger<AdoThreadReplier> logger) : IReviewThreadReplyPublisher
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public Task ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default)
    {
        AdoProviderAdapterHelpers.EnsureAzureDevOps(thread.Review.Repository.Host);

        if (!int.TryParse(thread.ExternalThreadId, out var threadId) || threadId < 1)
        {
            throw new InvalidOperationException("Azure DevOps review thread replies require a numeric thread identifier.");
        }

        return this.ReplyAcrossOrganizationsAsync(
            clientId,
            thread.Review.Repository.Host,
            AdoProviderAdapterHelpers.ResolveProjectId(thread.Review.Repository),
            thread.Review.Repository.ExternalRepositoryId,
            thread.Review.Number,
            threadId,
            replyText,
            ct);
    }

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

        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            cancellationToken);

        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken);
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

    private async Task ReplyAcrossOrganizationsAsync(
        Guid clientId,
        ProviderHostRef host,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string replyText,
        CancellationToken ct)
    {
        Exception? lastException = null;

        foreach (var organizationUrl in await AdoProviderAdapterHelpers.ResolveOrganizationUrlsAsync(
                     connectionRepository,
                     scopeRepository,
                     clientId,
                     host,
                     ct))
        {
            try
            {
                await this.ReplyAsync(
                    organizationUrl,
                    projectId,
                    repositoryId,
                    pullRequestId,
                    threadId,
                    replyText,
                    clientId,
                    ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        throw lastException ??
              new InvalidOperationException("No Azure DevOps organization URL could be resolved for thread replies.");
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AdoThreadReplier: posted reply to {OrganizationUrl} PR#{PullRequestId} thread {ThreadId}")]
    private static partial void LogReplied(ILogger logger, string organizationUrl, int pullRequestId, int threadId);
}
