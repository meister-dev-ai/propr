// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers;

public sealed class ReviewPreparationPolicyTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps, false)]
    [InlineData(ScmProvider.GitHub, false)]
    [InlineData(ScmProvider.GitLab, true)]
    [InlineData(ScmProvider.Forgejo, true)]
    public void LocalPreparation_PreservesInsertedAnchorsAndFindingProvenance(ScmProvider provider, bool downgrades)
    {
        var policy = ScmLocalPolicyFactory.CreateRegistry().GetCodeReviewPreparationPolicy(provider);
        var pullRequest = new PullRequest(
            "https://host.test", "project", "repo", "repo", 1, 1, "Review", null, "feature", "main",
            [new("src/file.cs", ChangeType.Edit, "added\ncontext", "@@ -1,1 +1,2 @@\n+added\n context", false)]);
        var inserted = new ReviewComment("/src/file.cs", 1, CommentSeverity.Warning, "Inserted");
        var context = new ReviewComment("/src/file.cs", 2, CommentSeverity.Warning, "Context")
        {
            ScopeRelation = ReviewCommentScopeRelation.OutsideChange,
            OriginModelId = "model",
            OriginPassShadow = true,
        };
        var result = policy.PrepareResult(pullRequest, new("Summary", [inserted, context]));
        Assert.Same(inserted, result.Result.Comments[0]);
        Assert.Equal(downgrades ? 1 : 0, result.DowngradedCount);
        var prepared = result.Result.Comments[1];
        Assert.Equal(context.ScopeRelation, prepared.ScopeRelation);
        Assert.Equal(context.OriginModelId, prepared.OriginModelId);
        Assert.Equal(context.OriginPassShadow, prepared.OriginPassShadow);
        Assert.Equal(downgrades ? null : context.FilePath, prepared.FilePath);
        Assert.Equal(downgrades ? "src/file.cs:L2: Context" : "Context", prepared.Message);
    }

    [Fact]
    public void LocalAzurePreparation_PreservesSyntheticRevisionAndComparisonIteration()
    {
        var registry = ScmLocalPolicyFactory.CreateRegistry();
        var policy = registry.GetCodeReviewPreparationPolicy(ScmProvider.AzureDevOps);
        var current = Job(ScmProvider.AzureDevOps, 4);
        var baseline = Job(ScmProvider.AzureDevOps, 1);
        baseline.SetReviewRevision(new("head", "base", null, "3", null));
        Assert.Equal(new ReviewRevision("ado-head-4", "ado-base-4", null, "4", null), policy.ResolveStoredRevision(current));
        Assert.False(policy.RequiresLiveRevisionRefresh(null));
        Assert.Equal(new ReviewComparisonHandle(true, 3), policy.SelectComparisonHandle(current, baseline));
        Assert.Equal(new ReviewComparisonHandle(false), policy.SelectComparisonHandle(baseline, current));
        Assert.Equal(typeof(ScmLocalPolicyFactory).Assembly, policy.CreatePublicationContext(3)!.GetType().Assembly);
        Assert.False(registry.IsRegistered(ScmProvider.AzureDevOps));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public void LocalCommitPreparation_PreservesCapturedRevisionAndMissingRevisionError(ScmProvider provider)
    {
        var policy = ScmLocalPolicyFactory.CreateRegistry().GetCodeReviewPreparationPolicy(provider);
        var job = Job(provider, 4);
        Assert.Equal(
            $"Review job {job.Id} is missing normalized review revision data for provider {provider}.",
            Assert.Throws<InvalidOperationException>(() => policy.ResolveStoredRevision(job)).Message);
        Assert.True(policy.RequiresLiveRevisionRefresh(null));
        var revision = new ReviewRevision("0123456789abcdef", "fedcba9876543210", null, "3", null);
        job.SetReviewRevision(revision);
        Assert.Equal(revision, policy.ResolveStoredRevision(job));
        Assert.False(policy.RequiresLiveRevisionRefresh(revision));
        Assert.Equal(new ReviewComparisonHandle(true, CompareToRevision: revision), policy.SelectComparisonHandle(job, job));
        Assert.Null(policy.CreatePublicationContext(3));
    }

    private static ReviewJob Job(ScmProvider provider, int iteration)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://host.test", "project", "repo", 1, iteration);
        var repository = ScmLocalPolicyFactory.CreateRegistry().GetReviewSourcePolicy(provider)
            .CreateRepository("https://host.test", "repo", "project", "repo");
        job.SetProviderReviewContext(new(repository, CodeReviewPlatformKind.PullRequest, "1", 1));
        return job;
    }
}
