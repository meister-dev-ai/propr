// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Intake;

public sealed class ProviderNeutralReviewJobContractTests
{
    [Fact]
    public void SubmitReviewJobRequestDto_CanAttachProviderNeutralContext_WithoutDroppingLegacyAdoFields()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");
        var reviewer = new ReviewerIdentity(host, "user-1", "meister-dev-bot", "Meister Dev Bot", true);

        var dto = new SubmitReviewJobRequestDto("https://dev.azure.com/org", "proj", "repo", 42, 7)
        {
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = repository,
            CodeReview = review,
            ReviewRevision = revision,
            RequestedReviewerIdentity = reviewer,
        };

        Assert.Equal("https://dev.azure.com/org", dto.ProviderScopePath);
        Assert.Equal("proj", dto.ProviderProjectKey);
        Assert.Equal(7, dto.IterationId);
        Assert.Equal(ScmProvider.GitHub, dto.Provider);
        Assert.Equal(host, dto.Host);
        Assert.Equal(repository, dto.Repository);
        Assert.Equal(review, dto.CodeReview);
        Assert.Equal(revision, dto.ReviewRevision);
        Assert.Equal(reviewer, dto.RequestedReviewerIdentity);
    }

    [Fact]
    public void ReviewJobStatusDto_CanExposeProviderNeutralTargetAndRevision()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");

        var dto = new ReviewJobStatusDto(
            Guid.NewGuid(),
            JobStatus.Pending,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            42,
            7,
            DateTimeOffset.Parse("2026-04-08T09:00:00Z"),
            null,
            null,
            null)
        {
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = repository,
            CodeReview = review,
            ReviewRevision = revision,
        };

        Assert.Equal(ScmProvider.GitHub, dto.Provider);
        Assert.Equal(host, dto.Host);
        Assert.Equal(repository, dto.Repository);
        Assert.Equal(review, dto.CodeReview);
        Assert.Equal(revision, dto.ReviewRevision);
    }

    [Fact]
    public void ReviewDiscoveryItemDto_CanCarryNormalizedReviewMetadata()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");
        var reviewer = new ReviewerIdentity(host, "user-1", "meister-dev-bot", "Meister Dev Bot", true);

        var dto = new ReviewDiscoveryItemDto(
            ScmProvider.GitHub,
            repository,
            review,
            CodeReviewState.Open,
            revision,
            reviewer,
            "Provider-neutral rollout",
            "https://github.com/acme/propr/pull/42",
            "refs/heads/feature/provider-neutral",
            "refs/heads/main");

        Assert.Equal(ScmProvider.GitHub, dto.Provider);
        Assert.Equal(repository, dto.Repository);
        Assert.Equal(review, dto.CodeReview);
        Assert.Equal(CodeReviewState.Open, dto.ReviewState);
        Assert.Equal(revision, dto.ReviewRevision);
        Assert.Equal(reviewer, dto.RequestedReviewerIdentity);
        Assert.Equal("Provider-neutral rollout", dto.Title);
    }
}
