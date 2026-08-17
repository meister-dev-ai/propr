// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

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

    public Task<string?> ReplyAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string replyText,
        CancellationToken ct = default,
        string? quotedComment = null)
    {
        // quotedComment is ignored. The interface carries it for the providers that answer with a new
        // comment on the pull request and need to show which comment they are answering. Azure DevOps posts
        // the reply into the thread, where the comment being answered is already directly above it.
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

    /// <inheritdoc cref="ReplyAsync(Guid, ReviewThreadRef, string, CancellationToken, string)" />
    public async Task<string?> ReplyAsync(
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
        if (connection.AuthorizedIdentity?.Id is { } authorizedIdentityId)
        {
            activity?.SetTag("publication.author.id", authorizedIdentityId.ToString("D"));
        }

        var gitClient = await connection.GetClientAsync<GitHttpClient>(cancellationToken);
        var renderedReplyText = FormatReplyText(replyText);

        var comment = new Comment
        {
            Content = renderedReplyText,
            CommentType = CommentType.Text,
        };

        var created = await gitClient.CreateCommentAsync(
            comment,
            repositoryId,
            pullRequestId,
            threadId,
            projectId,
            cancellationToken);

        LogReplied(logger, organizationUrl, pullRequestId, threadId);

        // Azure DevOps assigns the id server-side; a response without a positive one carries no comment to
        // record, so report none rather than a placeholder that would key a provenance row to nothing.
        return created is { Id: > 0 } ? created.Id.ToString(CultureInfo.InvariantCulture) : null;
    }

    private async Task<string?> ReplyAcrossOrganizationsAsync(
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
                return await this.ReplyAsync(
                    organizationUrl,
                    projectId,
                    repositoryId,
                    pullRequestId,
                    threadId,
                    replyText,
                    clientId,
                    ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        throw lastException ??
              new InvalidOperationException("No Azure DevOps organization URL could be resolved for thread replies.");
    }

    internal static string FormatReplyText(string replyText)
    {
        return HtmlSanitizer.RenderForDisplay(replyText, ReviewBodyRenderingMode.ThreadReply).RenderedText;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AdoThreadReplier: posted reply to {OrganizationUrl} PR#{PullRequestId} thread {ThreadId}")]
    private static partial void LogReplied(ILogger logger, string organizationUrl, int pullRequestId, int threadId);
}
