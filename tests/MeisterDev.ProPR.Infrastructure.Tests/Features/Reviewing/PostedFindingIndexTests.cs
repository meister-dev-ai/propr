// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.PostedFindings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing;

/// <summary>
///     The index that lets a later review increment recognise a concern an earlier one already posted.
///     Its contract is that it never throws: duplicate protection degrades, it does not fail a review.
/// </summary>
public sealed class PostedFindingIndexTests
{
    /// <summary>The host that issued the repository identifiers in this fixture.</summary>
    private const string Host = "https://provider.example";

    private const string Project = "project";

    private static readonly Guid ClientId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid JobId = new("bbbbbbbb-0000-0000-0000-000000000001");

    [Fact]
    public async Task FindDuplicateAsync_ClosestPostedFindingClearsTheThreshold_ReportsADuplicate()
    {
        var (embedder, repository, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        var postedFindingId = Guid.NewGuid();
        repository.FindClosestInPullRequestAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<float[]>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(new PostedFindingSimilarityDto(postedFindingId, "4242", 0.91f));

        var match = await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "The delete path races.");

        Assert.True(match.IsDuplicate);
        Assert.Equal("4242", match.ProviderThreadId);
        Assert.Equal(postedFindingId, match.PostedFindingId);
        Assert.Equal(0.91f, match.SimilarityScore);
        Assert.False(match.IsDegraded);
    }

    [Fact]
    public async Task FindDuplicateAsync_ScoreClearsTheMemoryFloorButNotItsOwn_IsANearMissNotADuplicate()
    {
        // Suppression has to be strictly more conservative than recall. Sharing one number is how a recall
        // knob turns into a precision incident, so a score good enough for reconsideration is not good enough
        // to keep a finding off the pull request.
        var (embedder, repository, index) = CreateIndex(postedFindingMinSimilarity: 0.85f, memoryMinSimilarity: 0.5f);
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repository.FindClosestInPullRequestAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<float[]>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(new PostedFindingSimilarityDto(Guid.NewGuid(), "4242", 0.62f));

        var match = await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "The delete path races.");

        Assert.False(match.IsDuplicate);
        Assert.Equal(0.62f, match.NearMissScore);
        Assert.Equal("4242", match.NearMissProviderThreadId);
    }

    [Fact]
    public async Task FindDuplicateAsync_AsksWithoutTheActingThreshold_SoAMissStillCarriesItsScore()
    {
        // The threshold is applied here, not in the query, because a query filtered by it returns nothing on a
        // miss and nothing is exactly what cannot be calibrated against.
        var (embedder, repository, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repository.FindClosestInPullRequestAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<float[]>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns((PostedFindingSimilarityDto?)null);

        await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "The delete path races.");

        await repository.Received(1)
            .FindClosestInPullRequestAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<float[]>(),
                0f,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDuplicateAsync_MatchOnAThreadProPrClosedItself_CarriesThatThrough()
    {
        var (embedder, repository, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repository.FindClosestInPullRequestAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<float[]>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(new PostedFindingSimilarityDto(Guid.NewGuid(), "4242", 0.91f, AutoResolvedByProPr: true));

        var match = await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "The delete path races.");

        Assert.True(match.IsDuplicate);
        Assert.True(match.AutoResolvedByProPr);
    }

    [Fact]
    public async Task FindDuplicateAsync_NothingClearsTheThreshold_ReportsNoDuplicate()
    {
        var (embedder, repository, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repository.FindClosestInPullRequestAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<float[]>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns((PostedFindingSimilarityDto?)null);

        var match = await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "Something else entirely.");

        Assert.False(match.IsDuplicate);
        Assert.False(match.IsDegraded);
    }

    [Fact]
    public async Task FindDuplicateAsync_EmbeddingUnavailable_DegradesWithoutThrowing()
    {
        var (embedder, _, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no embedding model bound"));

        var match = await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "The delete path races.");

        Assert.False(match.IsDuplicate);
        Assert.True(match.IsDegraded);
        Assert.Contains("posted_finding_index", match.DegradedComponents);
        Assert.NotNull(match.DegradedCause);
    }

    [Fact]
    public async Task FindDuplicateAsync_EmbeddingFailsOnce_SkipsFurtherEmbeddingForThatClient()
    {
        // One unbound embedding model must not cost an AI call per finding for the rest of the pass.
        var (embedder, _, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no embedding model bound"));

        await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "First finding.");
        var second = await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "Second finding.");

        await embedder.Received(1)
            .GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>());
        Assert.True(second.IsDegraded);
    }

    [Fact]
    public async Task FindDuplicateAsync_RepositoryUnavailable_DegradesWithoutThrowing()
    {
        var (embedder, repository, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repository.FindClosestInPullRequestAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<float[]>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var match = await index.FindDuplicateAsync(ClientId, Host, Project, "repo", 7, "The delete path races.");

        Assert.False(match.IsDuplicate);
        Assert.True(match.IsDegraded);
        Assert.Contains("posted_finding_index", match.DegradedComponents);
    }

    [Fact]
    public async Task RecordPostedFindingsAsync_EmbedsTheFindingTextAloneAndPersistsTheRow()
    {
        // The stored vector must carry no anchor and no severity, or the index cannot survive the drift it
        // exists to survive.
        var (embedder, repository, index) = CreateIndex();
        string? embedded = null;
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                embedded = call.ArgAt<string>(0);
                return new[] { 0.5f };
            });

        await index.RecordPostedFindingsAsync([Entry("The delete path races.")]);

        Assert.Equal("The delete path races.", embedded);
        await repository.Received(1)
            .AddMissingAsync(
                Arg.Is<IReadOnlyList<PostedFindingRecord>>(records =>
                    records.Count == 1 &&
                    records[0].ProviderThreadId == "4242" &&
                    records[0].ReviewJobId == JobId &&
                    records[0].IterationId == 3 &&
                    records[0].FindingMessage == "The delete path races." &&
                    records[0].FilePath == "/src/Agents.cs" &&
                    records[0].Severity == CommentSeverity.Error),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordPostedFindingsAsync_EmbeddingFails_AbandonsTheBatchWithoutThrowing()
    {
        // The realistic failure is a client with no embedding model bound, where every call fails the same
        // way. Discovering that once and stopping is worth more than one AI call per finding to rediscover
        // it. The cost is that a genuinely transient failure abandons the rest of the batch, which leaves
        // those findings unindexed and repeatable by the next increment.
        var (embedder, repository, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no embedding model bound"));

        var exception = await Record.ExceptionAsync(() => index.RecordPostedFindingsAsync([Entry("first", "1"), Entry("second", "2")]));

        Assert.Null(exception);
        await embedder.Received(1)
            .GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>());
        await repository.DidNotReceive()
            .AddMissingAsync(Arg.Any<IReadOnlyList<PostedFindingRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordPostedFindingsAsync_PersistenceFails_DoesNotThrow()
    {
        var (embedder, repository, index) = CreateIndex();
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repository.AddMissingAsync(Arg.Any<IReadOnlyList<PostedFindingRecord>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var exception = await Record.ExceptionAsync(() => index.RecordPostedFindingsAsync([Entry("The delete path races.")]));

        Assert.Null(exception);
    }

    private static PostedFindingEntry Entry(string message, string threadId = "4242")
    {
        return new PostedFindingEntry(
            ClientId,
            "repo",
            7,
            threadId,
            JobId,
            3,
            "/src/Agents.cs",
            CommentSeverity.Error,
            message);
    }

    private static (IThreadMemoryEmbedder Embedder, IPostedFindingRepository Repository, PostedFindingIndex Index)
        CreateIndex(float postedFindingMinSimilarity = 0.85f, float memoryMinSimilarity = 0.80f)
    {
        var embedder = Substitute.For<IThreadMemoryEmbedder>();
        var repository = Substitute.For<IPostedFindingRepository>();
        var options = Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions
            {
                PostedFindingMinSimilarity = postedFindingMinSimilarity,
                MemoryMinSimilarity = memoryMinSimilarity,
            });
        var logger = Substitute.For<ILogger<PostedFindingIndex>>();

        return (embedder, repository, new PostedFindingIndex(embedder, repository, options, logger));
    }
}
