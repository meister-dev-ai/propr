// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class ThreadResolvedProviderScopeTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://ado.example/tfs/collection", "https://ado.example", "AzureDevOps:https://ado.example/tfs/collection")]
    [InlineData(ScmProvider.GitHub, "https://github.example/team", "https://github.example", "GitHub:https://github.example")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example/team", "https://gitlab.example", "GitLab:https://gitlab.example")]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.example/team", "https://forgejo.example", "Forgejo:https://forgejo.example")]
    public async Task CrawlCarriesTheCapturedSourceIntoDispositionAndMemory(ScmProvider provider, string scopePath, string hostUrl, string expectedScope)
    {
        var harness = new Harness();
        var host = new ProviderHostRef(provider, hostUrl);
        var repository = new RepositoryRef(host, "repo-1", "project", "project");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var config = new CrawlConfigurationDto(Guid.NewGuid(), harness.ClientId, provider, scopePath, "project", 60, true, DateTimeOffset.UtcNow, []);
        var configs = Substitute.For<ICrawlConfigurationRepository>();
        configs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        var discovery = Substitute.For<IAssignedReviewDiscoveryService>();
        discovery.ListAssignedOpenReviewsAsync(config, Arg.Any<CancellationToken>())
            .Returns([new AssignedCodeReviewRef(host, repository, review, 7)]);
        var service = new PrCrawlService(
            configs, discovery, harness.Jobs, Substitute.For<IPrStatusFetcher>(), NullLogger<PrCrawlService>.Instance,
            new PullRequestSynchronizationService(
                MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
                harness.Jobs, NullLogger<PullRequestSynchronizationService>.Instance,
                threadStatusFetcher: harness.Threads, threadMemoryService: harness.Memory,
                prScanRepository: harness.Scans, codeInsightDispositionService: harness.Dispositions));

        await service.CrawlAsync();

        harness.AssertCapturedScope(expectedScope);
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://ado.example/tfs/collection", "https://ado.example", "AzureDevOps:https://ado.example/tfs/collection")]
    [InlineData(ScmProvider.GitHub, "https://github.example/team", "https://github.example", "GitHub:https://github.example")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example/team", "https://gitlab.example", "GitLab:https://gitlab.example")]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.example/team", "https://forgejo.example", "Forgejo:https://forgejo.example")]
    [InlineData(ScmProvider.GitHub, "https://github.example/team", null, null)]
    public async Task SynchronizationCarriesTheCapturedSourceWithoutReconstructingAnUnavailableHost(
        ScmProvider provider, string scopePath, string? hostUrl, string? expectedScope)
    {
        var harness = new Harness();
        var service = new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
            harness.Jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            threadStatusFetcher: harness.Threads, threadMemoryService: harness.Memory,
            prScanRepository: harness.Scans, codeInsightDispositionService: harness.Dispositions);
        var request = new PullRequestSynchronizationRequest
        {
            ActivationSource = PullRequestActivationSource.Webhook,
            SummaryLabel = "pull request updated",
            ClientId = harness.ClientId,
            Provider = provider,
            ProviderScopePath = scopePath,
            ProviderProjectKey = "project",
            RepositoryId = "repo-1",
            PullRequestId = 42,
            PullRequestStatus = PrStatus.Active,
            CandidateIterationId = 7,
            Host = hostUrl is null ? null : new ProviderHostRef(provider, hostUrl),
        };

        await service.SynchronizeAsync(request);

        harness.AssertCapturedScope(expectedScope);
    }

    private sealed class Harness
    {
        public Harness()
        {
            this.Jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
                .Returns(new TryAddReviewJobResult(true, null, 0));
            var scan = new ReviewPrScan(Guid.NewGuid(), this.ClientId, "https://provider.example", "project", "repo-1", 42, "7");
            scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scan.Id, ThreadId = "17", LastSeenStatus = "Active" });
            this.Scans.GetAsync(this.ClientId, Arg.Any<string>(), "project", "repo-1", 42, Arg.Any<CancellationToken>()).Returns(scan);
            this.Threads.GetReviewerThreadStatusesAsync(
                    Arg.Any<string>(), "project", "repo-1", 42, Arg.Any<ThreadOwnershipResolver>(), this.ClientId, Arg.Any<CancellationToken>())
                .Returns([new PrThreadStatusEntry("17", "ByDesign", "src/Service.cs", "Accepted by the reviewer.", 0)]);
        }

        public Guid ClientId { get; } = Guid.NewGuid();

        public IJobRepository Jobs { get; } = Substitute.For<IJobRepository>();

        public IReviewPrScanRepository Scans { get; } = Substitute.For<IReviewPrScanRepository>();

        public IReviewerThreadStatusFetcher Threads { get; } = Substitute.For<IReviewerThreadStatusFetcher>();

        public IThreadMemoryService Memory { get; } = Substitute.For<IThreadMemoryService>();

        public ICodeInsightDispositionService Dispositions { get; } = Substitute.For<ICodeInsightDispositionService>();

        public void AssertCapturedScope(string? expectedScope)
        {
            var memory = Assert.IsType<ThreadResolvedDomainEvent>(Assert.Single(this.Memory.ReceivedCalls()).GetArguments()[0]);
            var disposition = Assert.IsType<ThreadResolvedDomainEvent>(Assert.Single(this.Dispositions.ReceivedCalls()).GetArguments()[0]);
            Assert.Equal(expectedScope, disposition.ProviderScope);
            Assert.Equal(expectedScope, memory.ProviderScope);
            Assert.Equal(disposition.ThreadId, memory.ThreadId);
            Assert.Equal(disposition.Intent, memory.Intent);
            Assert.Equal(disposition.CodeChangedSinceRaised, memory.CodeChangedSinceRaised);
            Assert.Equal(disposition.NativeStatus, memory.NativeStatus);
        }
    }
}
