// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>Azure DevOps-backed implementation of <see cref="IReviewAssignmentService" />.</summary>
public sealed partial class AdoReviewerManager(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    ILogger<AdoReviewerManager> logger) : IReviewAssignmentService
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <summary>
    ///     Exposed as a delegate so tests can inject a mock without requiring a real VssConnection.
    /// </summary>
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public Task AddOptionalReviewerAsync(
        Guid clientId,
        CodeReviewRef review,
        ReviewerIdentity reviewer,
        CancellationToken ct = default)
    {
        AdoProviderAdapterHelpers.EnsureAzureDevOps(review.Repository.Host);

        if (!Guid.TryParse(reviewer.ExternalUserId, out var reviewerId))
        {
            throw new InvalidOperationException("Azure DevOps reviewer assignment requires a GUID-based reviewer identity.");
        }

        return this.AddOptionalReviewerAcrossOrganizationsAsync(
            clientId,
            review.Repository.Host,
            AdoProviderAdapterHelpers.ResolveProjectId(review.Repository),
            review.Repository.ExternalRepositoryId,
            review.Number,
            reviewerId,
            ct);
    }

    /// <inheritdoc />
    public async Task AddOptionalReviewerAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AdoReviewerManager.AddOptionalReviewer");
        activity?.SetTag("reviewer.id", reviewerId.ToString());
        activity?.SetTag("pr.id", pullRequestId);

        var gitClient = await this.ResolveGitClientAsync(organizationUrl, clientId, cancellationToken);

        // Check if the reviewer is already on the PR — ADO is idempotent but we avoid
        // the misleading log and the unnecessary API call.
        var existingReviewers = await gitClient.GetPullRequestReviewersAsync(
            repositoryId,
            pullRequestId,
            projectId,
            cancellationToken);

        if (existingReviewers?.Any(r => string.Equals(
                r.Id,
                reviewerId.ToString(),
                StringComparison.OrdinalIgnoreCase)) == true)
        {
            LogReviewerAlreadyPresent(logger, reviewerId, pullRequestId);
            return;
        }

        var reviewer = new IdentityRefWithVote
        {
            Id = reviewerId.ToString(),
            Vote = 0,
            IsRequired = false,
        };

        await gitClient.CreatePullRequestReviewerAsync(
            reviewer,
            repositoryId,
            pullRequestId,
            reviewerId.ToString(),
            projectId,
            cancellationToken);

        LogReviewerAdded(logger, reviewerId, pullRequestId);
    }

    private async Task AddOptionalReviewerAcrossOrganizationsAsync(
        Guid clientId,
        ProviderHostRef host,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
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
                await this.AddOptionalReviewerAsync(
                    organizationUrl,
                    projectId,
                    repositoryId,
                    pullRequestId,
                    reviewerId,
                    clientId,
                    ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("No Azure DevOps organization URL could be resolved for reviewer assignment.");
    }

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Added optional reviewer {ReviewerId} to PR #{PullRequestId}")]
    private static partial void LogReviewerAdded(
        ILogger logger,
        Guid reviewerId,
        int pullRequestId);

    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Trace,
        Message = "Reviewer {ReviewerId} is already on PR #{PullRequestId} — skipping add")]
    private static partial void LogReviewerAlreadyPresent(
        ILogger logger,
        Guid reviewerId,
        int pullRequestId);

    private async Task<GitHttpClient> ResolveGitClientAsync(
        string organizationUrl,
        Guid? clientId,
        CancellationToken ct)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            ct);

        if (this.GitClientResolver is not null)
        {
            return await this.GitClientResolver(organizationUrl, ct);
        }

        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }
}
