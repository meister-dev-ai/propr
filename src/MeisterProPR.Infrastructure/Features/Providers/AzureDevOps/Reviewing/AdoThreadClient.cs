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
///     Azure DevOps implementation of <see cref="IReviewThreadStatusWriter" />.
///     Updates pull request thread status using the ADO Git REST API PATCH endpoint.
/// </summary>
internal sealed partial class AdoThreadClient(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    ILogger<AdoThreadClient> logger) : IReviewThreadStatusWriter
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <summary>
    ///     Exposed for testing: allows injection of a <see cref="GitHttpClient" /> without a real VssConnection.
    /// </summary>
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public Task UpdateThreadStatusAsync(
        Guid clientId,
        ReviewThreadRef thread,
        string status,
        CancellationToken ct = default)
    {
        AdoProviderAdapterHelpers.EnsureAzureDevOps(thread.Review.Repository.Host);

        if (!int.TryParse(thread.ExternalThreadId, out var threadId) || threadId < 1)
        {
            throw new InvalidOperationException("Azure DevOps review thread status updates require a numeric thread identifier.");
        }

        return this.UpdateThreadStatusAcrossOrganizationsAsync(
            clientId,
            thread.Review.Repository.Host,
            AdoProviderAdapterHelpers.ResolveProjectId(thread.Review.Repository),
            thread.Review.Repository.ExternalRepositoryId,
            thread.Review.Number,
            threadId,
            status,
            ct);
    }

    /// <inheritdoc />
    public async Task UpdateThreadStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string status,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AdoThreadClient.UpdateThreadStatus");
        activity?.SetTag("ado.organization_url", organizationUrl);
        activity?.SetTag("ado.pull_request_id", pullRequestId);
        activity?.SetTag("ado.thread_id", threadId);
        activity?.SetTag("ado.status", status);

        var commentStatus = Enum.TryParse<CommentThreadStatus>(status, true, out var parsed)
            ? parsed
            : CommentThreadStatus.Unknown;

        var thread = new GitPullRequestCommentThread { Status = commentStatus };

        var gitClient = await this.ResolveGitClientAsync(organizationUrl, clientId, cancellationToken);

        await gitClient.UpdateThreadAsync(
            thread,
            projectId,
            repositoryId,
            pullRequestId,
            threadId,
            null,
            cancellationToken);

        LogStatusUpdated(logger, organizationUrl, pullRequestId, threadId, status);
    }

    private async Task UpdateThreadStatusAcrossOrganizationsAsync(
        Guid clientId,
        ProviderHostRef host,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string status,
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
                await this.UpdateThreadStatusAsync(
                    organizationUrl,
                    projectId,
                    repositoryId,
                    pullRequestId,
                    threadId,
                    status,
                    clientId,
                    ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("No Azure DevOps organization URL could be resolved for thread status updates.");
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "AdoThreadClient: set thread {ThreadId} on PR#{PullRequestId} in {OrganizationUrl} to status '{Status}'")]
    private static partial void LogStatusUpdated(
        ILogger logger,
        string organizationUrl,
        int pullRequestId,
        int threadId,
        string status);

    private async Task<GitHttpClient> ResolveGitClientAsync(
        string organizationUrl,
        Guid? clientId,
        CancellationToken ct)
    {
        if (this.GitClientResolver is not null)
        {
            return await this.GitClientResolver(organizationUrl, ct);
        }

        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            ct);

        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        await connection.ConnectAsync(ct);
        return connection.GetClient<GitHttpClient>();
    }
}
