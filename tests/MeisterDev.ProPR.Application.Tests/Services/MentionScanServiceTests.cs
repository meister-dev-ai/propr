// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Threading.Channels;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="MentionScanService" />.</summary>
public sealed class MentionScanServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ConfigId = Guid.NewGuid();
    private static readonly Guid ReviewerId = Guid.NewGuid();

    private const string CoveredRepositoryId = "repo";

    // Claimed a while ago, as a stored configuration always is. A filter with no claim time is treated as
    // claimed this instant, which answers nothing, so a fixture without one would test the wrong thing.
    private static readonly DateTimeOffset ClaimedAt = DateTimeOffset.UtcNow.AddDays(-7);

    private static readonly MentionConfigurationDto DefaultConfig = new(
        ConfigId,
        ClientId,
        ScmProvider.AzureDevOps,
        "https://dev.azure.com/org",
        "proj",
        60,
        true,
        DateTimeOffset.UtcNow,
        [new MentionRepoFilterDto(Guid.NewGuid(), CoveredRepositoryId, ClaimedAt: ClaimedAt)]);

    private readonly IActivePrFetcher _activePrFetcher = Substitute.For<IActivePrFetcher>();
    private readonly Channel<MentionReplyJob> _channel;
    private readonly IClientRegistry _clientRegistry = Substitute.For<IClientRegistry>();

    private readonly IMentionConfigurationRepository _mentionConfigs = Substitute.For<IMentionConfigurationRepository>();
    private readonly IMentionReplyJobRepository _jobRepository = Substitute.For<IMentionReplyJobRepository>();

    private readonly IProviderActivationService _providerActivationService =
        Substitute.For<IProviderActivationService>();

    private readonly IPullRequestFetcher _pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
    private readonly IMentionScanRepository _scanRepository = Substitute.For<IMentionScanRepository>();
    private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();
    private readonly MentionScanService _sut;

    public MentionScanServiceTests()
    {
        this._channel = Channel.CreateUnbounded<MentionReplyJob>();
        this._sut = new MentionScanService(
            this._mentionConfigs,
            this._activePrFetcher,
            this._pullRequestFetcher,
            this._clientRegistry,
            this._scanRepository,
            this._jobRepository,
            this._channel.Writer,
            NullLogger<MentionScanService>.Instance,
            this._providerActivationService,
            this._providerRegistry);

        this._providerActivationService.IsEnabledAsync(Arg.Any<ScmProvider>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // The ordinary case: a provider that replies inside the thread, and so needs the thread's identifier.
        this._providerRegistry.RequiresReviewThreadIdentifier(Arg.Any<ScmProvider>()).Returns(true);
        this._clientRegistry.GetEffectiveReviewerIdentityAsync(
                DefaultConfig.ClientId,
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(MentionedReviewer);
    }

    /// <summary>The identity the mention addresses, and so the account a reply must be attributed to.</summary>
    private static ReviewerIdentity MentionedReviewer { get; } =
        new(
            new ProviderHostRef(DefaultConfig.Provider, DefaultConfig.ProviderScopePath),
            ReviewerId.ToString("D"),
            ReviewerId.ToString("D"),
            ReviewerId.ToString("D"),
            false);

    [Fact]
    public async Task ScanAsync_DisabledProvider_SkipsConfiguration()
    {
        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._providerActivationService.IsEnabledAsync(DefaultConfig.Provider, Arg.Any<CancellationToken>())
            .Returns(false);

        await this._sut.ScanAsync();

        await this._activePrFetcher.DidNotReceive()
            .GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_AsksDiscoveryForTheProviderTheConfigurationNames()
    {
        var gitHubConfig = DefaultConfig with
        {
            Provider = ScmProvider.GitHub,
            ProviderScopePath = "https://github.com",
            ProviderProjectKey = "acme",
        };

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([gitHubConfig]);
        this._clientRegistry.GetEffectiveReviewerIdentityAsync(
                gitHubConfig.ClientId,
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(MentionedReviewer);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([], true));

        await this._sut.ScanAsync();

        await this._activePrFetcher.Received(1).GetRecentlyUpdatedPullRequestsAsync(
            Arg.Is<ActivePullRequestQuery>(query =>
                query.Provider == ScmProvider.GitHub
                && query.ScopePath == "https://github.com"
                && query.ClientId == gitHubConfig.ClientId),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A question asked in the pull request's conversation is as ordinary as one asked on a line of code.
    ///     GitHub and Forgejo keep those comments out of their review-thread listing, so the scan asks for
    ///     them separately; the ones that hold them in the thread listing answer this with nothing.
    /// </summary>
    [Fact]
    public async Task ScanAsync_MentionInThePullRequestConversation_IsAnswered()
    {
        var pr = new ActivePullRequestRef(
            DefaultConfig.ProviderScopePath,
            DefaultConfig.ProviderProjectKey,
            CoveredRepositoryId,
            42,
            DateTimeOffset.UtcNow);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));

        // The pull request itself carries no review threads: the question was asked in the conversation.
        this._pullRequestFetcher.FetchAsync(null!, null!, null!, 0, 0)
            .ReturnsForAnyArgs(MakeEmptyPullRequest(42));
        this._pullRequestFetcher.FetchConversationThreadsAsync(null!, null!, null!, 0)
            .ReturnsForAnyArgs<IReadOnlyList<PrCommentThread>>(
            [
                new PrCommentThread(
                    "9001",
                    null,
                    null,
                    [
                        new PrThreadComment(
                            "developer",
                            $"@<{ReviewerId}> What is this supposed to do?",
                            Guid.NewGuid(),
                            9001,
                            DateTimeOffset.UtcNow),
                    ]),
            ]);
        this._jobRepository.TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>()).Returns(true);

        await this._sut.ScanAsync();

        await this._jobRepository.Received(1).TryAddAsync(
            Arg.Is<MentionReplyJob>(job => job.ThreadId == "9001" && job.CommentId == 9001),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     An answer that quotes the question repeats the mention inside the quote. On a provider that hands
    ///     ProPR's own reply back as a readable comment, taking that for a new question would answer it, quote
    ///     the quote, and do it again on every scan.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ItsOwnQuotedAnswer_IsNotAnsweredAgain()
    {
        // What the reply publisher posts: the question quoted, with the answer under it.
        var ownAnswer = new PrThreadComment(
            "ProPR",
            $"> @<{ReviewerId}> What is this supposed to do?\n\nIt sorts ascending.",
            null,
            9002,
            DateTimeOffset.UtcNow);

        await this.ScanConversationCommentAsync(ownAnswer);

        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     An installation whose reviewer identity is an account a person also posts from is ordinary on a
    ///     small instance. The question is still a question, so what decides is the quote and not the author.
    /// </summary>
    [Fact]
    public async Task ScanAsync_QuestionFromTheAccountTheReviewerAnswersAs_IsStillAnswered()
    {
        var askedByTheSameAccount = new PrThreadComment(
            MentionedReviewer.Login,
            $"@<{ReviewerId}> What is this supposed to do?",
            ReviewerId,
            9003,
            DateTimeOffset.UtcNow);

        await this.ScanConversationCommentAsync(askedByTheSameAccount);

        await this._jobRepository.Received(1).TryAddAsync(
            Arg.Is<MentionReplyJob>(job => job.CommentId == 9003),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Claiming a repository takes effect from that moment, so a question asked before it is never
    ///     answered. That is deliberate, and it is the skip an operator testing a new configuration is most
    ///     likely to hit.
    /// </summary>
    [Fact]
    public async Task ScanAsync_MentionPublishedBeforeTheRepositoryWasClaimed_IsNotAnswered()
    {
        var askedBeforeTheClaim = new PrThreadComment(
            "developer",
            $"@<{ReviewerId}> What is this supposed to do?",
            Guid.NewGuid(),
            9005,
            ClaimedAt.AddMinutes(-1));

        await this.ScanConversationCommentAsync(askedBeforeTheClaim);

        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Quoting an earlier message to ask something new is asking something new.</summary>
    [Fact]
    public async Task ScanAsync_FollowUpAskedUnderAQuote_IsAnswered()
    {
        var followUp = new PrThreadComment(
            "developer",
            $"> It sorts ascending.\n\n@<{ReviewerId}> then why is it labelled latest?",
            Guid.NewGuid(),
            9004,
            DateTimeOffset.UtcNow);

        await this.ScanConversationCommentAsync(followUp);

        await this._jobRepository.Received(1).TryAddAsync(
            Arg.Is<MentionReplyJob>(job => job.CommentId == 9004),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Runs one scan over a single comment in the pull request's conversation.</summary>
    private async Task ScanConversationCommentAsync(PrThreadComment comment)
    {
        var pr = new ActivePullRequestRef(
            DefaultConfig.ProviderScopePath,
            DefaultConfig.ProviderProjectKey,
            CoveredRepositoryId,
            42,
            DateTimeOffset.UtcNow);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(null!, null!, null!, 0, 0)
            .ReturnsForAnyArgs(MakeEmptyPullRequest(42));
        this._pullRequestFetcher.FetchConversationThreadsAsync(null!, null!, null!, 0)
            .ReturnsForAnyArgs<IReadOnlyList<PrCommentThread>>(
            [
                new PrCommentThread(
                    comment.CommentId.ToString(CultureInfo.InvariantCulture),
                    null,
                    null,
                    [comment]),
            ]);
        this._jobRepository.TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>()).Returns(true);

        await this._sut.ScanAsync();
    }

    [Fact]
    public async Task ScanAsync_AsksDiscoveryOnlyAboutTheClaimedRepositories()
    {
        var config = DefaultConfig with
        {
            RepoFilters =
            [
                new MentionRepoFilterDto(Guid.NewGuid(), "101", DisplayName: "acme/platform", ClaimedAt: ClaimedAt),
                new MentionRepoFilterDto(Guid.NewGuid(), "202", DisplayName: "acme/tooling", ClaimedAt: ClaimedAt),
            ],
        };

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([], true));

        await this._sut.ScanAsync();

        await this._activePrFetcher.Received(1).GetRecentlyUpdatedPullRequestsAsync(
            Arg.Is<ActivePullRequestQuery>(query =>
                query.Repositories.Count == 2
                && query.Repositories[0].RepositoryId == "101"
                && query.Repositories[0].DisplayName == "acme/platform"
                && query.Repositories[1].RepositoryId == "202"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_PrInAnUnclaimedRepository_IsLeftUnread()
    {
        var unclaimed = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            "someone-elses-repo",
            9,
            DateTimeOffset.UtcNow);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([unclaimed], true));

        await this._sut.ScanAsync();

        await this._pullRequestFetcher.DidNotReceiveWithAnyArgs().FetchAsync(null!, null!, null!, 0, 0);
    }

    [Fact]
    public async Task ScanAsync_DiscoveryUnavailableForOneConfiguration_StillScansTheOthers()
    {
        var unsupportedConfig = DefaultConfig with
        {
            Id = Guid.NewGuid(),
            Provider = ScmProvider.Forgejo,
            ProviderScopePath = "https://forgejo.example.com",
        };

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([unsupportedConfig, DefaultConfig]);
        this._clientRegistry.GetEffectiveReviewerIdentityAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(MentionedReviewer);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                callInfo.Arg<ActivePullRequestQuery>().Provider == ScmProvider.Forgejo
                    ? throw new InvalidOperationException("No active pull-request discovery is registered for provider Forgejo.")
                    : new ActivePullRequestDiscovery([], true));

        await this._sut.ScanAsync();

        // The configuration that could not be served is reported and left; the cycle still reaches the rest.
        await this._activePrFetcher.Received(1).GetRecentlyUpdatedPullRequestsAsync(
            Arg.Is<ActivePullRequestQuery>(query => query.Provider == ScmProvider.AzureDevOps),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_NoPrsSinceWatermark_SkipsThreadFetch()
    {
        // Arrange: no PRs returned since watermark
        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId)
            .ReturnsForAnyArgs(new MentionProjectScan(Guid.NewGuid(), ConfigId, DateTimeOffset.UtcNow));
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([], true));

        // Act
        await this._sut.ScanAsync();

        // Assert: no thread fetching occurred
        await this._pullRequestFetcher.DidNotReceiveWithAnyArgs().FetchAsync(null!, null!, null!, 0, 0);
    }

    [Fact]
    public async Task ScanAsync_PrWithOlderTimestampThanWatermark_SkipsPr()
    {
        // Arrange: PR last updated before the pr-level watermark → skip
        var lastSeen = DateTimeOffset.UtcNow;
        var pr = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            42,
            lastSeen.AddMinutes(-5));

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._scanRepository.GetPrScanAsync(ConfigId, "repo", 42)
            .ReturnsForAnyArgs(new MentionPrScan(Guid.NewGuid(), ConfigId, "repo", 42, lastSeen));

        // Act
        await this._sut.ScanAsync();

        // Assert: no thread fetching occurred for the skipped PR
        await this._pullRequestFetcher.DidNotReceiveWithAnyArgs().FetchAsync(null!, null!, null!, 0, 0);
    }

    [Fact]
    public async Task ScanAsync_MentionFoundAndNotDuplicate_EnqueuesJob()
    {
        // Arrange: PR with comment mentioning reviewer GUID
        var pr = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            DateTimeOffset.UtcNow);
        var mentionContent = $"@<{ReviewerId}> what does this do?";
        var thread = new PrCommentThread(
            "100",
            null,
            null,
            [new PrThreadComment("Alice", mentionContent, Guid.NewGuid(), 200, DateTimeOffset.UtcNow)]);
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            "desc",
            "feature/x",
            "main",
            [],
            ExistingThreads: [thread]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(ConfigId, "repo", 1).ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pullRequest);
        this._jobRepository.ExistsForCommentAsync(
                "repo",
                1,
                "100",
                200,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        this._jobRepository.TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await this._sut.ScanAsync();

        // Assert: job was added to repo and channel
        await this._jobRepository.Received(1)
            .TryAddAsync(
                Arg.Is<MentionReplyJob>(j =>
                    j.ClientId == ClientId &&
                    j.RepositoryId == "repo" &&
                    j.ThreadId == "100" &&
                    j.CommentId == 200 &&
                    // The exact key, not merely a populated one. This is what decides which account owns the
                    // answer, so a wrong-but-present value is the failure worth catching.
                    j.MentionedReviewerKey == MentionedReviewer.AddressedKey),
                Arg.Any<CancellationToken>());
        Assert.Equal(1, this._channel.Reader.Count);
    }

    [Fact]
    public async Task ScanAsync_MentionAlreadyProcessed_DoesNotEnqueueDuplicate()
    {
        // Arrange: ExistsForCommentAsync returns true → duplicate detection prevents re-enqueue
        var pr = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            DateTimeOffset.UtcNow);
        var mentionContent = $"@<{ReviewerId}> same question";
        var thread = new PrCommentThread(
            "100",
            null,
            null,
            [new PrThreadComment("Bob", mentionContent, Guid.NewGuid(), 201, DateTimeOffset.UtcNow)]);
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "PR",
            null,
            "b",
            "main",
            [],
            ExistingThreads: [thread]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(ConfigId, "repo", 1).ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pullRequest);
        this._jobRepository.ExistsForCommentAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await this._sut.ScanAsync();

        // Assert: no job was created (duplicate suppressed)
        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, this._channel.Reader.Count);
    }

    [Fact]
    public async Task ScanAsync_AfterCycle_UpsertsBothWatermarks()
    {
        // Arrange: one PR, no threads
        var pr = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            5,
            DateTimeOffset.UtcNow);
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            5,
            1,
            "Empty PR",
            null,
            "b",
            "main",
            [],
            ExistingThreads: []);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pullRequest);

        // Act
        await this._sut.ScanAsync();

        // Assert: both project and PR watermarks were upserted
        await this._scanRepository.Received(1)
            .UpsertProjectScanAsync(
                Arg.Is<MentionProjectScan>(s => s.MentionConfigurationId == ConfigId),
                Arg.Any<CancellationToken>());
        await this._scanRepository.Received(1)
            .UpsertPrScanAsync(
                Arg.Is<MentionPrScan>(s => s.MentionConfigurationId == ConfigId && s.PullRequestId == 5),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_PullRequestInAnUnclaimedRepository_IsNeverRead()
    {
        // The provider lists the whole project. A client that did not claim this repository must not read
        // its conversations, which on a shared project belong to somebody else.
        var pr = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            "someone-elses-repo",
            9,
            DateTimeOffset.UtcNow);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));

        await this._sut.ScanAsync();

        await this._pullRequestFetcher.DidNotReceiveWithAnyArgs().FetchAsync(null!, null!, null!, 0, 0);
    }

    [Fact]
    public async Task ScanAsync_RepositoryClaimedWithDifferentCasing_IsStillRead()
    {
        // Providers are inconsistent about the casing of an identifier between endpoints, and a claim that
        // stopped matching because of it would silently stop answering.
        var pr = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            CoveredRepositoryId.ToUpperInvariant(),
            11,
            DateTimeOffset.UtcNow);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(MakeEmptyPullRequest(11));

        await this._sut.ScanAsync();

        await this._pullRequestFetcher.ReceivedWithAnyArgs(1).FetchAsync(null!, null!, null!, 0, 0);
    }

    [Fact]
    public async Task ScanAsync_AnotherClientTookTheCommentFirst_DoesNotQueueItsOwnJob()
    {
        // Both clients cover the repository and both were right to look. The database decides, and the
        // client that loses must not queue a job whose answer would duplicate the winner's.
        var pr = new ActivePullRequestRef(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            DateTimeOffset.UtcNow);
        var thread = new PrCommentThread(
            "100",
            null,
            null,
            [new PrThreadComment("Alice", $"@<{ReviewerId}> who answers?", Guid.NewGuid(), 202, DateTimeOffset.UtcNow)]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(MakeEmptyPullRequest(1) with { ExistingThreads = [thread] });

        // Nothing existed when the check ran; the write is what discovers the loss.
        this._jobRepository.ExistsForCommentAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        this._jobRepository.TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await this._sut.ScanAsync();

        // The write really was attempted and really was lost. Asserting only the empty channel would pass
        // just as well if the scan had never reached the comment at all, which is the opposite behaviour.
        await this._jobRepository.Received(1)
            .TryAddAsync(
                Arg.Is<MentionReplyJob>(j =>
                    j.RepositoryId == CoveredRepositoryId &&
                    j.CommentId == 202 &&
                    j.MentionedReviewerKey == MentionedReviewer.AddressedKey),
                Arg.Any<CancellationToken>());
        Assert.Equal(0, this._channel.Reader.Count);
    }

    [Fact]
    public async Task ScanAsync_FirstScanOfAClaimedRepository_LeavesOlderQuestionsAlone()
    {
        // The provider hands back every open pull request whatever its age and there is no watermark yet,
        // so without a floor the first scan would answer, and bill for, every question the repository has
        // ever been asked. Claiming a repository says what happens from now on.
        var claimedAt = DateTimeOffset.UtcNow;
        var config = DefaultConfig with
        {
            RepoFilters = [new MentionRepoFilterDto(Guid.NewGuid(), CoveredRepositoryId, ClaimedAt: claimedAt)],
        };
        var pr = new ActivePullRequestRef("https://dev.azure.com/org", "proj", "repo", 1, DateTimeOffset.UtcNow);
        var oldQuestion = new PrCommentThread(
            "100",
            null,
            null,
            [
                new PrThreadComment(
                    "Alice",
                    $"@<{ReviewerId}> asked long before the repository was claimed",
                    Guid.NewGuid(),
                    200,
                    claimedAt.AddDays(-30)),
            ]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(MakeEmptyPullRequest(1) with { ExistingThreads = [oldQuestion] });

        await this._sut.ScanAsync();

        // The pull request really was opened and its question really was read. Without this, a regression
        // that skipped the pull request outright would satisfy the assertion below and look like the floor
        // working.
        await this._pullRequestFetcher.ReceivedWithAnyArgs(1)
            .FetchAsync(default!, default!, default!, default, default, default, default, default);
        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, this._channel.Reader.Count);
    }

    [Fact]
    public async Task ScanAsync_QuestionTheProviderDoesNotDate_LeavesItAlone()
    {
        // A comment with no timestamp cannot be shown to fall after the claim, and the whole backlog of a
        // freshly claimed repository would arrive that way if a provider stopped dating comments.
        var claimedAt = DateTimeOffset.UtcNow;
        var config = DefaultConfig with
        {
            RepoFilters = [new MentionRepoFilterDto(Guid.NewGuid(), CoveredRepositoryId, ClaimedAt: claimedAt)],
        };
        var pr = new ActivePullRequestRef("https://dev.azure.com/org", "proj", "repo", 1, DateTimeOffset.UtcNow);
        var undatedQuestion = new PrCommentThread(
            "100",
            null,
            null,
            [new PrThreadComment("Alice", $"@<{ReviewerId}> undated", Guid.NewGuid(), 200, PublishedAt: null)]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(MakeEmptyPullRequest(1) with { ExistingThreads = [undatedQuestion] });

        await this._sut.ScanAsync();

        // As above: the undated question was read and then declined, not missed.
        await this._pullRequestFetcher.ReceivedWithAnyArgs(1)
            .FetchAsync(default!, default!, default!, default, default, default, default, default);
        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, this._channel.Reader.Count);
    }

    [Fact]
    public async Task ScanAsync_QuestionAskedAfterTheRepositoryWasClaimed_IsAnswered()
    {
        var claimedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var config = DefaultConfig with
        {
            RepoFilters = [new MentionRepoFilterDto(Guid.NewGuid(), CoveredRepositoryId, ClaimedAt: claimedAt)],
        };
        var pr = new ActivePullRequestRef("https://dev.azure.com/org", "proj", "repo", 1, DateTimeOffset.UtcNow);
        var freshQuestion = new PrCommentThread(
            "100",
            null,
            null,
            [
                new PrThreadComment(
                    "Alice",
                    $"@<{ReviewerId}> asked after the claim",
                    Guid.NewGuid(),
                    201,
                    claimedAt.AddHours(2)),
            ]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._scanRepository.GetPrScanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .ReturnsForAnyArgs((MentionPrScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(MakeEmptyPullRequest(1) with { ExistingThreads = [freshQuestion] });
        this._jobRepository.TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>()).Returns(true);

        await this._sut.ScanAsync();

        await this._jobRepository.Received(1)
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_RepositoryUnclaimedThenClaimedAgain_LeavesTheGapAlone()
    {
        // The scan rows survive a repository being dropped from a configuration, so a stale watermark from
        // before the gap would otherwise beat the newer claim and every question asked while nobody was
        // answering would be picked up.
        var reclaimedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var config = DefaultConfig with
        {
            RepoFilters = [new MentionRepoFilterDto(Guid.NewGuid(), CoveredRepositoryId, ClaimedAt: reclaimedAt)],
        };
        var pr = new ActivePullRequestRef("https://dev.azure.com/org", "proj", "repo", 1, DateTimeOffset.UtcNow);
        var askedWhileUnclaimed = new PrCommentThread(
            "100",
            null,
            null,
            [
                new PrThreadComment(
                    "Alice",
                    $"@<{ReviewerId}> asked while nobody was answering",
                    Guid.NewGuid(),
                    202,
                    reclaimedAt.AddMinutes(-30)),
            ]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);

        // The watermark left behind from before the repository was dropped.
        this._scanRepository.GetPrScanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .ReturnsForAnyArgs(
                new MentionPrScan(
                    Guid.NewGuid(),
                    ConfigId,
                    "repo",
                    1,
                    reclaimedAt.AddDays(-3)));
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([pr], true));
        this._pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(MakeEmptyPullRequest(1) with { ExistingThreads = [askedWhileUnclaimed] });

        await this._sut.ScanAsync();

        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A tick that could not read everything leaves its discovery window open. Closing it would step over
    ///     whatever was asked in the part that failed: discovery asks for what changed since the watermark, and
    ///     a pull request nobody touches again never changes.
    /// </summary>
    [Fact]
    public async Task ScanAsync_TickThatDidNotReadEverything_LeavesTheDiscoveryWindowOpen()
    {
        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([], false));

        await this._sut.ScanAsync();

        // Scanned, so its interval advances and the next tick does not scan it again immediately.
        await this._scanRepository.Received(1).UpsertProjectScanAsync(
            Arg.Is<MentionProjectScan>(scan => scan.LastScannedAt > DateTimeOffset.MinValue
                                               && scan.LastCompleteScanAt == null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The same tick, read in full, closes the window over the ground it covered.</summary>
    [Fact]
    public async Task ScanAsync_TickThatReadEverything_ClosesTheDiscoveryWindow()
    {
        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new ActivePullRequestDiscovery([], true));

        await this._sut.ScanAsync();

        await this._scanRepository.Received(1).UpsertProjectScanAsync(
            Arg.Is<MentionProjectScan>(scan => scan.LastCompleteScanAt != null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A conversation that could not be listed must not move the pull request's watermark. The watermark is
    ///     a floor on how old an answerable comment may be, so moving it over an unread conversation refuses
    ///     every question in it from then on.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ConversationThatCouldNotBeRead_DoesNotMoveTheWatermarkPastIt()
    {
        var codeComment = new PrCommentThread(
            "thread-1",
            "src/Program.cs",
            405,
            [
                new PrThreadComment(
                    "developer",
                    "This sorts ascending.",
                    Guid.NewGuid(),
                    9100,

                    // Newer than the question waiting unread in the conversation, so a watermark taken from
                    // the threads that were read would bury it.
                    DateTimeOffset.UtcNow),
            ]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(
                new ActivePullRequestDiscovery(
                    [
                        new ActivePullRequestRef(
                            DefaultConfig.ProviderScopePath,
                            DefaultConfig.ProviderProjectKey,
                            CoveredRepositoryId,
                            42,
                            DateTimeOffset.UtcNow),
                    ],
                    true));
        this._pullRequestFetcher.FetchAsync(null!, null!, null!, 0, 0)
            .ReturnsForAnyArgs(MakeEmptyPullRequest(42) with { ExistingThreads = [codeComment] });
        this._pullRequestFetcher.FetchConversationThreadsAsync(null!, null!, null!, 0)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("the timeline could not be listed"));

        await this._sut.ScanAsync();

        await this._scanRepository.DidNotReceiveWithAnyArgs()
            .UpsertPrScanAsync(Arg.Any<MentionPrScan>(), Arg.Any<CancellationToken>());

        // And the configuration's window stays open too, so the pull request is offered again next tick.
        await this._scanRepository.Received(1).UpsertProjectScanAsync(
            Arg.Is<MentionProjectScan>(scan => scan.LastCompleteScanAt == null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The loop a quote cannot close. A provider that replies inside the thread posts no quote at all, so
    ///     an answer that names the reviewer in its own words is indistinguishable from a question by its text.
    ///     What separates them is that ProPR recorded posting this comment.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ItsOwnUnquotedAnswer_IsNotAnsweredAgain()
    {
        this._jobRepository.GetPostedReplyCommentIdsAsync(
                CoveredRepositoryId,
                42,
                Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal) { "9002" });

        // No quote anywhere, and the handle repeated in the answer's own prose.
        var ownAnswer = new PrThreadComment(
            "ProPR",
            $"You asked @<{ReviewerId}> what this does: it sorts ascending.",
            null,
            9002,
            DateTimeOffset.UtcNow);

        await this.ScanConversationCommentAsync(ownAnswer);

        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The same text from a comment ProPR did not post is a question. The guard is keyed on what was
    ///     posted, so it cannot turn away a developer who happens to write the way an answer does.
    /// </summary>
    [Fact]
    public async Task ScanAsync_AQuestionResemblingAnAnswerProPrDidNotPost_IsStillAnswered()
    {
        this._jobRepository.GetPostedReplyCommentIdsAsync(
                CoveredRepositoryId,
                42,
                Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal) { "9002" });

        var askedByAPerson = new PrThreadComment(
            "developer",
            $"You asked @<{ReviewerId}> what this does: it sorts ascending. Is that right?",
            Guid.NewGuid(),
            9003,
            DateTimeOffset.UtcNow);

        await this.ScanConversationCommentAsync(askedByAPerson);

        await this._jobRepository.Received(1).TryAddAsync(
            Arg.Is<MentionReplyJob>(job => job.CommentId == 9003),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Forgejo names no thread for a comment on a line of code, and its reply publisher needs none: it
    ///     answers on the pull request and says which comment it answers with a quote. So the question is
    ///     answered, keyed on the comment's own identifier.
    /// </summary>
    [Fact]
    public async Task ScanAsync_QuestionOnALineOfCodeWhereTheProviderNamesNoThread_IsAnswered()
    {
        this._providerRegistry.RequiresReviewThreadIdentifier(ScmProvider.Forgejo).Returns(false);

        await this.ScanForgejoCodeCommentAsync();

        await this._jobRepository.Received(1).TryAddAsync(
            Arg.Is<MentionReplyJob>(job => job.ThreadId == "9004" && job.CommentId == 9004),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The substitute is not offered to a provider whose publisher addresses a thread: a job built on an
    ///     identifier it cannot post into would spend an answer and then fail to publish it.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ThreadWithNoIdentifierWhereReplyingNeedsOne_IsNotAnswered()
    {
        this._providerRegistry.RequiresReviewThreadIdentifier(ScmProvider.Forgejo).Returns(true);

        await this.ScanForgejoCodeCommentAsync();

        await this._jobRepository.DidNotReceiveWithAnyArgs()
            .TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Scans one Forgejo question asked on a line of code, in a thread the provider gives no identifier.
    /// </summary>
    private async Task ScanForgejoCodeCommentAsync()
    {
        var config = new MentionConfigurationDto(
            ConfigId,
            ClientId,
            ScmProvider.Forgejo,
            "https://forgejo.example",
            "acme",
            60,
            true,
            DateTimeOffset.UtcNow,
            [new MentionRepoFilterDto(Guid.NewGuid(), CoveredRepositoryId, ClaimedAt: ClaimedAt)]);

        var host = new ProviderHostRef(ScmProvider.Forgejo, config.ProviderScopePath);
        this._clientRegistry.GetEffectiveReviewerIdentityAsync(
                config.ClientId,
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, "7", "propr", "ProPR", false));

        var codeQuestion = new PrCommentThread(
            // What ForgejoPullRequestFetcher reports: no thread, because Forgejo names none here.
            null,
            "src/Program.cs",
            405,
            [
                new PrThreadComment(
                    "developer",
                    "@propr what does this do?",
                    Guid.NewGuid(),
                    9004,
                    DateTimeOffset.UtcNow),
            ]);

        this._mentionConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<ActivePullRequestQuery>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(
                new ActivePullRequestDiscovery(
                    [
                        new ActivePullRequestRef(
                            config.ProviderScopePath,
                            config.ProviderProjectKey,
                            CoveredRepositoryId,
                            42,
                            DateTimeOffset.UtcNow),
                    ],
                    true));
        this._pullRequestFetcher.FetchAsync(null!, null!, null!, 0, 0)
            .ReturnsForAnyArgs(MakeEmptyPullRequest(42) with { ExistingThreads = [codeQuestion] });
        this._jobRepository.TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>()).Returns(true);

        await this._sut.ScanAsync();
    }

    private static PullRequest MakeEmptyPullRequest(int pullRequestId)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            pullRequestId,
            1,
            "PR",
            null,
            "b",
            "main",
            [],
            ExistingThreads: []);
    }
}
