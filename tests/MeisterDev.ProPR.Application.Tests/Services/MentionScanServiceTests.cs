// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
            this._providerActivationService);

        this._providerActivationService.IsEnabledAsync(Arg.Any<ScmProvider>(), Arg.Any<CancellationToken>())
            .Returns(true);
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([]));

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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));

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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
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
