// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceSourceUrlTests
{
    [Theory]
    [InlineData("https://github.test/owner")]
    [InlineData("owner")]
    public async Task NativeScopeCoordinatesDoNotDuplicateTheOwnerInTheLink(string providerScopePath)
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var job = Job(client, ScmProvider.GitHub, "https://github.test", "owner", "repo", "owner/repo");
        AddExclusion(db, job, Scope(job));
        db.Add(job);
        db.Entry(job).Property(row => row.OrganizationUrl).CurrentValue = providerScopePath;
        await db.SaveChangesAsync();
        Assert.Equal("https://github.test/owner/repo/pull/17", (await Entry(db, client)).SourceUrl);
    }

    [Theory]
    [InlineData("mailto:service@example.test")]
    [InlineData("file:///tmp/invalid")]
    public async Task MalformedRetainedLinkCoordinatesDoNotInvalidateOtherMeasurements(string malformedHost)
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var bad = Job(client, ScmProvider.GitHub, "https://scm.test/deploy", "owner", "repo", "owner/repo");
        AddExclusion(db, bad, Scope(bad));
        db.Add(bad);
        db.Entry(bad).Property(row => row.HostBaseUrl).CurrentValue = malformedHost;
        var healthy = new CodeInsightPullRequest { Id = Guid.NewGuid(), ClientId = client, RepositoryId = "healthy", PullRequestId = 18 };
        db.Add(healthy);
        foreach (var outcome in new[] { "collectionComplete", "positive" })
        {
            db.Add(
                new ReviewerPerformanceDailyCount
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = healthy.Id, ClientId = client, RepositoryId = healthy.RepositoryId,
                    PullRequestId = 18, ProviderScope = "GitHub:https://scm.test", BucketDate = new(2026, 10, 1), Outcome = outcome,
                    Count = 1, PublicationState = CodeInsightPublicationState.Published,
                });
        }

        await db.SaveChangesAsync();
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(LocalScmPolicies.Registry)
            .AddSingleton<IOptionsMonitor<CodeInsightsOptions>>(new StaticOptionsMonitor<CodeInsightsOptions>(new() { MinimumCoveragePercentage = 0 }))
            .BuildServiceProvider();
        var response = await ActivatorUtilities.CreateInstance<ReviewerPerformanceRangeReader>(services).QueryAsync(
            new() { Views = [new() { From = new(2026, 10, 1), To = new(2026, 10, 1) }] }, [client]);
        Assert.NotNull(response.Views[0].Series[0].Points[0].Score.Summary.F1);
        Assert.Null(Assert.Single(response.Views[0].ExclusionCatalog!).SourceUrl);
    }

    [Theory]
    [InlineData("owner/%")]
    [InlineData("owner/%E0%A4%A")]
    [InlineData("owner/%ZZ")]
    public async Task MalformedPercentEscapesInOptionalCapturedPathsDoNotFailTheEvidenceRead(string path)
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var job = Job(client, ScmProvider.GitHub, "https://github.test", "owner", "repo", "owner/repo");
        AddExclusion(db, job, Scope(job));
        db.Add(job);
        db.Entry(job).Property(row => row.OrganizationUrl).CurrentValue = path;
        await db.SaveChangesAsync();

        var entry = await Entry(db, client);

        Assert.Equal(client, entry.ClientId);
        Assert.Equal(17, entry.PullRequestId);
        Assert.Contains("miss-collection-coverage-unavailable", entry.Reasons);
        Assert.Equal("GitHub:https://github.test", entry.ProviderScope);
    }

    [Fact]
    public async Task LegacyAzureDevOpsCoordinatesDoNotRequireAnOptionalHost()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var job = Job(client, ScmProvider.AzureDevOps, "https://scm.test/collection", "Team Project", "repo", "Team Project");
        AddExclusion(db, job, Scope(job), repositoryName: "repo");
        db.Add(job);
        db.Entry(job).Property(row => row.HostBaseUrl).CurrentValue = null;
        await db.SaveChangesAsync();
        Assert.Equal("https://scm.test/collection/Team%20Project/_git/repo/pullrequest/17", (await Entry(db, client)).SourceUrl);
    }

    [Fact]
    public async Task AzureDevOpsLinksUseTheRepositoryNameAndNeedOne()
    {
        // Azure DevOps opens no pull request when the path carries the repository identifier.
        await using var named = Db();
        var client = Guid.NewGuid();
        var job = Job(client, ScmProvider.AzureDevOps, "https://dev.azure.com/acme", "Team Project", Guid.NewGuid().ToString("D"), "Team Project");
        AddExclusion(named, job, Scope(job), repositoryName: "payments-api");
        named.Add(job);
        await named.SaveChangesAsync();
        Assert.Equal("https://dev.azure.com/acme/Team%20Project/_git/payments-api/pullrequest/17", (await Entry(named, client)).SourceUrl);

        await using var unnamed = Db();
        var other = Job(client, ScmProvider.AzureDevOps, "https://dev.azure.com/acme", "Team Project", Guid.NewGuid().ToString("D"), "Team Project");
        AddExclusion(unnamed, other, Scope(other));
        unnamed.Add(other);
        await unnamed.SaveChangesAsync();
        Assert.Null((await Entry(unnamed, client)).SourceUrl);
    }

    [Theory]
    [InlineData(
        ScmProvider.AzureDevOps, "https://scm.test/collection", "Team Project", "repo id", "Team Project",
        "https://scm.test/collection/Team%20Project/_git/repo%20name/pullrequest/17")]
    [InlineData(ScmProvider.GitHub, "https://scm.test/deploy", "owner", "123", "owner/repo name", "https://scm.test/deploy/owner/repo%20name/pull/17")]
    [InlineData(
        ScmProvider.GitLab, "https://scm.test/deploy", "group", "123", "group/subgroup/repo",
        "https://scm.test/deploy/group/subgroup/repo/-/merge_requests/17")]
    [InlineData(ScmProvider.Forgejo, "https://scm.test/deploy", "owner", "123", "owner/repo", "https://scm.test/deploy/owner/repo/pulls/17")]
    public async Task ExclusionLinksUseTheCapturedProviderCoordinates(
        ScmProvider provider, string host, string project, string repository, string path, string expected)
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var job = Job(client, provider, host, project, repository, path);
        AddExclusion(db, job, Scope(job), repositoryName: "repo name");
        db.Add(job);
        db.Entry(job).Property(row => row.HostBaseUrl).CurrentValue = host;
        await db.SaveChangesAsync();
        Assert.Equal(expected, (await Entry(db, client)).SourceUrl);
    }

    [Theory]
    [InlineData("foreign-scope")]
    [InlineData("credentials")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("ambiguous")]
    [InlineData("foreign-client")]
    [InlineData("host-mismatch")]
    [InlineData("encoded-control")]
    [InlineData("traversal")]
    public async Task UntrustedOrAmbiguousCoordinatesDoNotProduceLinks(string issue)
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var source = issue switch
        {
            "credentials" => "https://user:private@scm.test/deploy",
            "query" => "https://scm.test/deploy?private=token",
            "fragment" => "https://scm.test/deploy#private",
            "encoded-control" => "https://scm.test/deploy/%0a",
            _ => "https://scm.test/deploy",
        };
        var job = Job(
            issue == "foreign-client" ? Guid.NewGuid() : client, ScmProvider.GitHub, source, "owner", "repo",
            issue == "traversal" ? "owner/../repo" : "owner/repo");
        if (issue == "host-mismatch")
        {
            job = new ReviewJob(
                new CodeReviewSourceContext(
                    ScmProvider.GitHub, "https://other.test", "owner", "owner/repo",
                    CodeReviewPlatformKind.PullRequest, "17"), Guid.NewGuid(), client, source, "owner", "repo", 17, 1);
        }

        var scope = issue == "foreign-scope" ? "GitHub:https://other.test" : Scope(job);
        AddExclusion(db, job, scope, client);
        db.Add(job);
        if (issue == "ambiguous")
        {
            db.Add(Job(client, ScmProvider.GitHub, source, "owner", "repo", "other/repo"));
        }

        await db.SaveChangesAsync();
        Assert.Null((await Entry(db, client)).SourceUrl);
    }

    private static ReviewJob Job(Guid client, ScmProvider provider, string host, string project, string repository, string path) => new(
        new CodeReviewSourceContext(
            provider, host, project, path, provider == ScmProvider.GitLab ? CodeReviewPlatformKind.MergeRequest : CodeReviewPlatformKind.PullRequest, "17"),
        Guid.NewGuid(), client, host, project, repository, 17, 1);

    private static string Scope(ReviewJob job) =>
        ProviderSourceIdentity.FromReviewJob(job, LocalScmPolicies.Registry.GetSourceIdentityPolicy(job.Provider)).Value;

    private static void AddExclusion(MeisterProPRDbContext db, ReviewJob job, string scope, Guid? client = null, string? repositoryName = null)
    {
        var aggregate = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(), ClientId = client ?? job.ClientId, RepositoryId = job.RepositoryId, PullRequestId = job.PullRequestId,
            RepositoryName = repositoryName,
        };
        db.AddRange(
            aggregate, new ReviewerPerformanceDailyCount
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = aggregate.Id, ClientId = aggregate.ClientId, RepositoryId = aggregate.RepositoryId,
                PullRequestId = aggregate.PullRequestId, ProviderScope = scope, BucketDate = new(2026, 10, 1), Outcome = "collectionUnknown", Count = 1,
            });
    }

    private static async Task<ReviewerPerformanceExclusion> Entry(MeisterProPRDbContext db, Guid client)
    {
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(LocalScmPolicies.Registry).BuildServiceProvider();
        var reader = ActivatorUtilities.CreateInstance<ReviewerPerformanceRangeReader>(services);
        var response = await reader.QueryAsync(new() { Views = [new() { From = new(2026, 10, 1), To = new(2026, 10, 1) }] }, [client]);
        return Assert.Single(response.Views[0].ExclusionCatalog!);
    }

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
