// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Runtime;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

/// <summary>
///     A thread pass reads the latest revision of its pull request through the adapter of the pull request's own
///     provider before it judges any thread, and skips itself when the pull request has moved to a later revision.
///     The registry with its latest-revision readers, the thread pass and the Azure DevOps iteration resolver are the
///     production classes; only the provider calls behind them are substituted.
/// </summary>
public sealed class ThreadPassLatestRevisionTests
{
    private static readonly Guid ClientId = Guid.Parse("5f0c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f");
    private const int PullRequestId = 42;
    private const string QueuedHead = "1111111111111111111111111111111111111111";
    private const string LaterHead = "2222222222222222222222222222222222222222";
    private const string BaseSha = "3333333333333333333333333333333333333333";
    private const string MovedReason = "The pull request moved to a later revision before the pass ran.";

    [Theory]
    [InlineData(ScmProvider.GitHub, "https://github.com")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example.com")]
    public async Task ProcessAsync_HeadMovedPastTheQueuedRevision_SkipsThePassWithoutAnIterationLookup(
        ScmProvider provider,
        string hostBaseUrl)
    {
        var harness = Harness.ForCommitProvider(provider, hostBaseUrl);
        harness.WithLatestHead(provider, LaterHead);

        await harness.RunAsync();

        await harness.ThreadPassJobs.Received(1).SetSkippedAsync(harness.Job.Id, MovedReason, Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.DidNotReceive().SetCompletedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);

        // The Azure DevOps resolver reads the client's connections before anything else. A connection repository
        // without calls proves that the thread pass made no iteration lookup for this pull request.
        await harness.Connections.DidNotReceiveWithAnyArgs().GetByClientIdAsync(default, default);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, "https://github.com")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example.com")]
    public async Task ProcessAsync_HeadStillAtTheQueuedRevision_RunsThePassWithoutAWarning(
        ScmProvider provider,
        string hostBaseUrl)
    {
        var harness = Harness.ForCommitProvider(provider, hostBaseUrl);
        harness.WithLatestHead(provider, QueuedHead);

        await harness.RunAsync();

        await harness.ThreadPassJobs.DidNotReceive().SetSkippedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1).SetCompletedAsync(harness.Job.Id, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Level >= LogLevel.Warning);
        await harness.Connections.DidNotReceiveWithAnyArgs().GetByClientIdAsync(default, default);
    }

    [Fact]
    public async Task ProcessAsync_AzureDevOpsIterationMovedPastTheQueuedOne_SkipsThePass()
    {
        var harness = Harness.ForAzureDevOps(queuedIterationId: 7);
        harness.WithLatestIteration(8);

        await harness.RunAsync();

        await harness.ThreadPassJobs.Received(1).SetSkippedAsync(harness.Job.Id, MovedReason, Arg.Any<CancellationToken>());
        await harness.IterationResolver.Received(1).GetLatestIterationIdAsync(
            ClientId,
            Harness.AdoOrganizationUrl,
            Harness.AdoProject,
            Harness.AdoRepository,
            PullRequestId,
            Arg.Any<CancellationToken>());
        await harness.QueryService(ScmProvider.AzureDevOps).DidNotReceiveWithAnyArgs()
            .GetLatestRevisionAsync(default, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_AzureDevOpsIterationStillTheQueuedOne_RunsThePass()
    {
        var harness = Harness.ForAzureDevOps(queuedIterationId: 7);
        harness.WithLatestIteration(7);

        await harness.RunAsync();

        await harness.ThreadPassJobs.DidNotReceive().SetSkippedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1).SetCompletedAsync(harness.Job.Id, Arg.Any<CancellationToken>());
        await harness.QueryService(ScmProvider.AzureDevOps).DidNotReceiveWithAnyArgs()
            .GetLatestRevisionAsync(default, default!, default);
    }

    private sealed class Harness
    {
        public const string AdoOrganizationUrl = "https://dev.azure.com/acme";
        public const string AdoProject = "project";
        public const string AdoRepository = "repo-1";

        private static readonly ScmProvider[] Providers = [ScmProvider.AzureDevOps, ScmProvider.GitHub, ScmProvider.GitLab];

        private readonly IReviewPrScanThreadPassStore _prScans = Substitute.For<IReviewPrScanThreadPassStore>();
        private readonly IPullRequestFetcher _fetcher = Substitute.For<IPullRequestFetcher>();
        private readonly IClientRegistry _clients = Substitute.For<IClientRegistry>();
        private readonly Dictionary<ScmProvider, ICodeReviewQueryService> _queryServices = [];
        private readonly List<IReviewThreadStatusWriter> _statusWriters = [];

        private Harness(ThreadPassJob job, IPullRequestIterationResolver iterationResolver, IClientScmConnectionRepository connections)
        {
            this.Job = job;
            this.IterationResolver = iterationResolver;
            this.Connections = connections;

            foreach (var provider in Providers)
            {
                var queryService = Substitute.For<ICodeReviewQueryService>();
                queryService.Provider.Returns(provider);
                this._queryServices[provider] = queryService;

                var statusWriter = Substitute.For<IReviewThreadStatusWriter>();
                statusWriter.Provider.Returns(provider);
                this._statusWriters.Add(statusWriter);
            }

            this._clients.GetCommentResolutionBehaviorAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(CommentResolutionBehavior.Silent);
            this.ThreadPassJobs.TryBeginAttemptAsync(job.Id, Arg.Any<CancellationToken>()).Returns(true);
            this.ThreadPassJobs.SetCompletedAsync(job.Id, Arg.Any<CancellationToken>()).Returns(true);
            this.ThreadPassJobs.SetSkippedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            this.ThreadPassJobs.GetHandledThreadKeysAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns([]);

            // An active pull request without reviewer threads: a pass that gets past the revision check completes
            // at once, so whether the check let it through is visible on the job row.
            this._fetcher.FetchThreadContextAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<bool>())
                .Returns(
                    new PullRequest(
                        job.OrganizationUrl,
                        job.ProjectId,
                        job.RepositoryId,
                        "widgets",
                        job.PullRequestId,
                        job.IterationId,
                        "Title",
                        null,
                        "feature",
                        "main",
                        [],
                        PrStatus.Active,
                        [],
                        AuthorizedIdentityName: "review-bot"));
        }

        public ThreadPassJob Job { get; }

        public IPullRequestIterationResolver IterationResolver { get; }

        public IThreadPassJobRepository ThreadPassJobs { get; } = Substitute.For<IThreadPassJobRepository>();

        public IClientScmConnectionRepository Connections { get; }

        public RecordingLogger Log { get; } = new();

        /// <summary>
        ///     A pass queued at <see cref="QueuedHead" /> on a provider whose revisions are commits. The iteration
        ///     resolver is the production one, and the client's only connection to the host belongs to that provider,
        ///     so an iteration lookup for this pull request fails the way it does in production.
        /// </summary>
        public static Harness ForCommitProvider(ScmProvider provider, string hostBaseUrl)
        {
            var host = new ProviderHostRef(provider, hostBaseUrl);
            var review = new CodeReviewRef(
                new RepositoryRef(host, "101", "acme", "acme/widgets"),
                CodeReviewPlatformKind.PullRequest,
                "9001",
                PullRequestId);
            var job = new ThreadPassJob(
                review,
                Guid.NewGuid(),
                ClientId,
                $"{hostBaseUrl}/acme",
                "acme",
                "101",
                PullRequestId,
                1,
                QueuedHead,
                $"{QueuedHead}|17:1");

            var connections = Substitute.For<IClientScmConnectionRepository>();
            connections.GetByClientIdAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns([Connection(provider, hostBaseUrl)]);

            // No VssConnectionFactory is supplied: the resolver refuses a host of another provider before it
            // opens a connection, which is the failure a thread pass on GitHub ran into in production.
            var resolver = new AdoPullRequestIterationResolver(
                null!,
                connections,
                NullLogger<AdoPullRequestIterationResolver>.Instance);
            return new Harness(job, resolver, connections);
        }

        /// <summary>A pass queued at the given Azure DevOps iteration, with a substituted iteration resolver.</summary>
        public static Harness ForAzureDevOps(int queuedIterationId)
        {
            var job = new ThreadPassJob(
                Guid.NewGuid(),
                ClientId,
                AdoOrganizationUrl,
                AdoProject,
                AdoRepository,
                PullRequestId,
                queuedIterationId,
                queuedIterationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{queuedIterationId}|17:1");
            return new Harness(
                job,
                Substitute.For<IPullRequestIterationResolver>(),
                Substitute.For<IClientScmConnectionRepository>());
        }

        public ICodeReviewQueryService QueryService(ScmProvider provider) => this._queryServices[provider];

        /// <summary>Has the provider report the given head commit as the latest revision of the pull request.</summary>
        public void WithLatestHead(ScmProvider provider, string headSha)
        {
            this._queryServices[provider]
                .GetLatestRevisionAsync(ClientId, Arg.Is<CodeReviewRef>(review => review.Number == PullRequestId), Arg.Any<CancellationToken>())
                .Returns(new ReviewRevision(headSha, BaseSha, null, headSha, $"{BaseSha}...{headSha}"));
        }

        /// <summary>Has Azure DevOps report the given iteration as the latest one of the pull request.</summary>
        public void WithLatestIteration(int iterationId)
        {
            this.IterationResolver.GetLatestIterationIdAsync(
                    ClientId,
                    AdoOrganizationUrl,
                    AdoProject,
                    AdoRepository,
                    PullRequestId,
                    Arg.Any<CancellationToken>())
                .Returns(iterationId);
        }

        public Task RunAsync() => this.CreateService().ProcessAsync(this.Job, CancellationToken.None);

        private ThreadPassService CreateService()
        {
            var registry = new ScmProviderRegistry(
                [],
                [],
                this._queryServices.Values,
                [],
                [],
                [],
                [],
                this._statusWriters,
                [],
                [],
                [],
                [],
                identityPolicies: ScmLocalPolicyFactory.CreateIdentityPolicies(),
                latestRevisionReaders: [new AdoLatestRevisionReader(this.IterationResolver)]);

            return new ThreadPassService(
                this.ThreadPassJobs,
                this._prScans,
                this._fetcher,
                registry,
                this._clients,
                Substitute.For<IAiCommentResolutionCore>(),
                Substitute.For<IProtocolRecorder>(),
                this.Log);
        }

        private static ClientScmConnectionDto Connection(ScmProvider provider, string hostBaseUrl)
        {
            return new ClientScmConnectionDto(
                Guid.NewGuid(),
                ClientId,
                provider,
                hostBaseUrl,
                ScmAuthenticationKind.PersonalAccessToken,
                provider.ToString(),
                true,
                "verified",
                DateTimeOffset.UtcNow,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
        }
    }

    private sealed class RecordingLogger : ILogger<ThreadPassService>
    {
        public List<(LogLevel Level, EventId EventId, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Entries.Add((logLevel, eventId, exception));
        }
    }
}
