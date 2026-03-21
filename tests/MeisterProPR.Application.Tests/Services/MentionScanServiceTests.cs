using System.Threading.Channels;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="MentionScanService" />.</summary>
public sealed class MentionScanServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ConfigId = Guid.NewGuid();
    private static readonly Guid ReviewerId = Guid.NewGuid();

    private static readonly CrawlConfigurationDto DefaultConfig = new(
        ConfigId,
        ClientId,
        "https://dev.azure.com/org",
        "proj",
        ReviewerId,
        60,
        true,
        DateTimeOffset.UtcNow);

    private readonly IActivePrFetcher _activePrFetcher = Substitute.For<IActivePrFetcher>();
    private readonly Channel<MentionReplyJob> _channel;

    private readonly ICrawlConfigurationRepository _crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
    private readonly IMentionReplyJobRepository _jobRepository = Substitute.For<IMentionReplyJobRepository>();
    private readonly IPullRequestFetcher _pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
    private readonly IMentionScanRepository _scanRepository = Substitute.For<IMentionScanRepository>();
    private readonly MentionScanService _sut;

    public MentionScanServiceTests()
    {
        this._channel = Channel.CreateUnbounded<MentionReplyJob>();
        this._sut = new MentionScanService(
            this._crawlConfigs,
            this._activePrFetcher,
            this._pullRequestFetcher,
            this._scanRepository,
            this._jobRepository,
            this._channel.Writer,
            NullLogger<MentionScanService>.Instance);
    }

    [Fact]
    public async Task ScanAsync_NoPrsSinceWatermark_SkipsThreadFetch()
    {
        // Arrange: no PRs returned since watermark
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs(new MentionProjectScan(Guid.NewGuid(), ConfigId, DateTimeOffset.UtcNow));
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
        await this._pullRequestFetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default!, default!, default, default);
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

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._scanRepository.GetProjectScanAsync(ConfigId).ReturnsForAnyArgs((MentionProjectScan?)null);
        this._activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([pr]));
        this._scanRepository.GetPrScanAsync(ConfigId, "repo", 42).ReturnsForAnyArgs(new MentionPrScan(Guid.NewGuid(), ConfigId, "repo", 42, lastSeen));

        // Act
        await this._sut.ScanAsync();

        // Assert: no thread fetching occurred for the skipped PR
        await this._pullRequestFetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default!, default!, default, default);
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
            100,
            null,
            null,
            [new PrThreadComment("Alice", mentionContent, Guid.NewGuid(), 200, DateTimeOffset.UtcNow)]);
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "Test PR",
            "desc",
            "feature/x",
            "main",
            [],
            ExistingThreads: [thread]);

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pullRequest);
        this._jobRepository.ExistsForCommentAsync(ClientId, 1, 100, 200, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        await this._sut.ScanAsync();

        // Assert: job was added to repo and channel
        await this._jobRepository.Received(1)
            .AddAsync(
                Arg.Is<MentionReplyJob>(j =>
                    j.ClientId == ClientId &&
                    j.ThreadId == 100 &&
                    j.CommentId == 200),
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
            100,
            null,
            null,
            [new PrThreadComment("Bob", mentionContent, Guid.NewGuid(), 201, DateTimeOffset.UtcNow)]);
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "PR",
            null,
            "b",
            "main",
            [],
            ExistingThreads: [thread]);

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pullRequest);
        this._jobRepository.ExistsForCommentAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await this._sut.ScanAsync();

        // Assert: AddAsync was NOT called (duplicate suppressed)
        await this._jobRepository.DidNotReceiveWithAnyArgs().AddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>());
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
            5,
            1,
            "Empty PR",
            null,
            "b",
            "main",
            [],
            ExistingThreads: []);

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pullRequest);

        // Act
        await this._sut.ScanAsync();

        // Assert: both project and PR watermarks were upserted
        await this._scanRepository.Received(1)
            .UpsertProjectScanAsync(
                Arg.Is<MentionProjectScan>(s => s.CrawlConfigurationId == ConfigId),
                Arg.Any<CancellationToken>());
        await this._scanRepository.Received(1)
            .UpsertPrScanAsync(
                Arg.Is<MentionPrScan>(s => s.CrawlConfigurationId == ConfigId && s.PullRequestId == 5),
                Arg.Any<CancellationToken>());
    }
}
