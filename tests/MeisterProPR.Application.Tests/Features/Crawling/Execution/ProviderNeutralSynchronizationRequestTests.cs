// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Crawling.Execution;

public sealed class ProviderNeutralSynchronizationRequestTests
{
    [Fact]
    public void PullRequestSynchronizationRequest_CanCarryProviderScopedIdentifiers_AlongsideNormalizedReviewContext()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");
        var reviewer = new ReviewerIdentity(host, "user-1", "meister-dev-bot", "Meister Dev Bot", true);

        var request = new PullRequestSynchronizationRequest
        {
            ActivationSource = PullRequestActivationSource.Webhook,
            SummaryLabel = "pull request updated",
            ClientId = Guid.NewGuid(),
            ProviderScopePath = "https://dev.azure.com/org",
            ProviderProjectKey = "proj",
            RepositoryId = "repo-1",
            PullRequestId = 42,
            PullRequestStatus = PrStatus.Active,
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = repository,
            CodeReview = review,
            ReviewRevision = revision,
            ReviewState = CodeReviewState.Open,
            RequestedReviewerIdentity = reviewer,
        };

        Assert.Equal(ScmProvider.GitHub, request.Provider);
        Assert.Equal(host, request.Host);
        Assert.Equal(repository, request.Repository);
        Assert.Equal(review, request.CodeReview);
        Assert.Equal(revision, request.ReviewRevision);
        Assert.Equal(CodeReviewState.Open, request.ReviewState);
        Assert.Equal(reviewer, request.RequestedReviewerIdentity);
        Assert.Equal("https://dev.azure.com/org", request.ProviderScopePath);
    }

    [Fact]
    public void PullRequestSynchronizationRequest_DefaultsProviderToAzureDevOps_WhenNormalizedReviewContextMissing()
    {
        var request = new PullRequestSynchronizationRequest
        {
            ActivationSource = PullRequestActivationSource.Crawl,
            SummaryLabel = "crawl discovery",
            ClientId = Guid.NewGuid(),
            ProviderScopePath = "https://dev.azure.com/org",
            ProviderProjectKey = "proj",
            RepositoryId = "repo-1",
            PullRequestId = 42,
            PullRequestStatus = PrStatus.Active,
        };

        Assert.Equal(ScmProvider.AzureDevOps, request.Provider);
        Assert.Null(request.Host);
        Assert.Null(request.Repository);
        Assert.Null(request.CodeReview);
        Assert.Null(request.ReviewRevision);
        Assert.Null(request.ReviewState);
        Assert.Null(request.RequestedReviewerIdentity);
    }
}
