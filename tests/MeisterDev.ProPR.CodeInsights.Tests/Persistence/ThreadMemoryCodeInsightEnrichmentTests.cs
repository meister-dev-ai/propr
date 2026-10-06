// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

/// <summary>
///     Finding links and keywords enrich stored memories without changing the memory storage decision.
/// </summary>
public sealed class ThreadMemoryCodeInsightEnrichmentTests
{
    /// <summary>The host that issued the repository identifiers in this fixture.</summary>
    private const string Host = "https://provider.example";

    private const string Project = "project";

    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FindingId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownSourceLinksOnlyItsFindingWhenThreadIdentifiersOverlap(bool matchingFindingExists)
    {
        await using var db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var pr = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            RepositoryId = "repo-1",
            PullRequestId = 7,
        };
        db.Add(pr);
        db.Add(Finding(pr.Id, "GitHub:https://other.example"));
        var matching = Finding(pr.Id, "GitHub:https://provider.example");
        if (matchingFindingExists)
        {
            db.Add(matching);
        }

        await db.SaveChangesAsync();
        var codec = Substitute.For<ISecretProtectionCodec>();
        codec.Unprotect(Arg.Any<string>(), Arg.Any<string>()).Returns("The null check is missing.");
        var harness = new Harness(findingStore: new CodeInsightFindingStore(db, codec));

        await harness.Service.HandleThreadResolvedAsync(Resolved(providerScope: matching.ProviderScope));

        var stored = await harness.CapturedRecordAsync();
        Assert.Equal(matchingFindingExists ? matching.Id : (Guid?)null, stored.CodeInsightFindingId);
        Assert.Equal(["null-check", "authentication"], stored.Keywords);
        Assert.Equal("Resolved by adding the null check.", stored.ResolutionSummary);
    }

    [Fact]
    public async Task UnavailableSourceRetainsTheUnqualifiedLookup()
    {
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved());

        Assert.Equal(FindingId, (await harness.CapturedRecordAsync()).CodeInsightFindingId);
        await harness.Store.Received(1).FindByProviderThreadAsync(ClientId, "repo-1", 7, "9001", Arg.Any<CancellationToken>(), null);
    }

    [Fact]
    public async Task AMemoryFromAProPrFinding_CarriesTheFindingLinkAndItsKeywords()
    {
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved());

        var stored = await harness.CapturedRecordAsync();
        Assert.Equal(FindingId, stored.CodeInsightFindingId);
        Assert.Equal(["null-check", "authentication"], stored.Keywords);
    }

    [Fact]
    public async Task AMemoryWithNoMatchingFinding_IsStoredWithNoLink()
    {
        // The ordinary case for a human thread, an admin dismissal, or a thread raised before collection was
        // enabled. The memory is unaffected; only the link is absent.
        var harness = new Harness();
        harness.Store
            .FindByProviderThreadAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
            .Returns((CodeInsightFindingView?)null);

        await harness.Service.HandleThreadResolvedAsync(Resolved(providerScope: "GitHub:https://provider.example"));

        var stored = await harness.CapturedRecordAsync();
        Assert.Null(stored.CodeInsightFindingId);
    }

    [Fact]
    public async Task WithTheGateClosed_TheFindingLinkIsSkippedButKeywordsAreStillExtracted()
    {
        var harness = new Harness();
        harness.Gate.IsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>()).Returns(false);

        await harness.Service.HandleThreadResolvedAsync(Resolved(providerScope: "GitHub:https://provider.example"));

        var stored = await harness.CapturedRecordAsync();
        // The finding link is Code Insights data and stays behind the gate. Keywords are search metadata on a
        // memory, which is part of the base product, so a client without insights still gets them.
        Assert.Null(stored.CodeInsightFindingId);
        Assert.Equal(["null-check", "authentication"], stored.Keywords);
        await harness.Store.DidNotReceive().FindByProviderThreadAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
        await harness.KeywordExtractor.Received(1).ExtractAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingFindingLookupStillStoresTheMemory()
    {
        // A failed finding lookup must not prevent memory storage.
        var harness = new Harness();
        harness.Store
            .FindByProviderThreadAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("the insight store is unreachable"));

        await harness.Service.HandleThreadResolvedAsync(Resolved(providerScope: "GitHub:https://provider.example"));

        var stored = await harness.CapturedRecordAsync();
        Assert.Equal("Resolved by adding the null check.", stored.ResolutionSummary);
        Assert.Null(stored.CodeInsightFindingId);
    }

    [Fact]
    public async Task AFailingKeywordExtractionStillStoresTheMemory()
    {
        var harness = new Harness();
        harness.KeywordExtractor
            .ExtractAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the model is unreachable"));

        await harness.Service.HandleThreadResolvedAsync(Resolved());

        var stored = await harness.CapturedRecordAsync();
        Assert.Empty(stored.Keywords);
        Assert.Equal(FindingId, stored.CodeInsightFindingId);
    }

    [Fact]
    public async Task TheEnrichmentNeverChangesWhetherAMemoryIsStored()
    {
        // A close that claims a fix without a corroborating change is refused by thread memory on purpose.
        // Enrichment must not make that decision differently: the disposition path records that case instead.
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged));

        await harness.Repository.DidNotReceive()
            .UpsertAsync(Arg.Any<ThreadMemoryRecord>(), Arg.Any<CancellationToken>());
        await harness.KeywordExtractor.DidNotReceive().ExtractAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheFindingIsLookedUpByTheThreadIdsInvariantStringForm()
    {
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved());

        await harness.Store.Received(1).FindByProviderThreadAsync(
            ClientId,
            "repo-1",
            7,
            "9001",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutTheOptionalDependencies_MemoryStorageIsUnchanged()
    {
        // The whole slice is absent on an installation that never registered it.
        var harness = new Harness(withCodeInsights: false);

        await harness.Service.HandleThreadResolvedAsync(Resolved());

        var stored = await harness.CapturedRecordAsync();
        Assert.Null(stored.CodeInsightFindingId);
        Assert.Empty(stored.Keywords);
    }

    private static ThreadResolvedDomainEvent Resolved(
        ThreadResolutionIntent intent = ThreadResolutionIntent.AcceptedByHuman,
        ThreadAnchorCodeChange codeChange = ThreadAnchorCodeChange.Unknown,
        string? providerScope = null)
    {
        return new ThreadResolvedDomainEvent(
            ClientId,
            Host,
            Project,
            "repo-1",
            7,
            "9001",
            "src/Service.cs",
            "@@ -1 +1 @@",
            "alice: needs a null check\nbob: by design, the caller guarantees it",
            DateTimeOffset.UtcNow,
            intent,
            codeChange,
            ProviderScope: providerScope);
    }

    private static CodeInsightFinding Finding(Guid pullRequestId, string scope)
    {
        return new CodeInsightFinding
        {
            Id = Guid.NewGuid(),
            CodeInsightPullRequestId = pullRequestId,
            JobId = Guid.NewGuid(),
            RevisionKey = Guid.NewGuid().ToString(),
            ProviderScope = scope,
            ProviderThreadId = "9001",
        };
    }

    private sealed class Harness
    {
        public Harness(bool withCodeInsights = true, ICodeInsightFindingStore? findingStore = null)
        {
            this.Repository = Substitute.For<IThreadMemoryRepository>();
            this.Store = Substitute.For<ICodeInsightFindingStore>();
            this.KeywordExtractor = Substitute.For<IMemoryKeywordExtractor>();
            this.Gate = Substitute.For<ICodeInsightsCollectionGate>();

            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Store
                .FindByProviderThreadAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<long>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<string?>())
                .Returns(
                    new CodeInsightFindingView(
                        FindingId,
                        Guid.NewGuid(),
                        "rev-1",
                        0,
                        "src/Service.cs",
                        42,
                        CommentSeverity.Error,
                        "The null check is missing.",
                        "9001",
                        DateTimeOffset.UtcNow));
            this.KeywordExtractor
                .ExtractAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new[] { "null-check", "authentication" });

            var embedder = Substitute.For<IThreadMemoryEmbedder>();
            embedder.GenerateResolutionSummaryAsync(
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<string>(),
                    Arg.Any<Guid>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    new ThreadResolutionSummary(
                        "Resolved by adding the null check.",
                        ResolutionClarity.AcceptedWithoutChange));
            embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns([0.1f, 0.2f]);

            this.Service = new ThreadMemoryService(
                embedder,
                this.Repository,
                Substitute.For<IProtocolRecorder>(),
                Substitute.For<IMemoryActivityLog>(),
                Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
                NullLogger<ThreadMemoryService>.Instance,
                Substitute.For<IMemoryReconsiderationPromptBuilder>(),
                codeInsightFindingStore: withCodeInsights ? findingStore ?? this.Store : null,
                memoryKeywordExtractor: withCodeInsights ? this.KeywordExtractor : null,
                codeInsightsCollectionGate: withCodeInsights ? this.Gate : null);
        }

        public IThreadMemoryRepository Repository { get; }

        public ICodeInsightFindingStore Store { get; }

        public IMemoryKeywordExtractor KeywordExtractor { get; }

        public ICodeInsightsCollectionGate Gate { get; }

        public ThreadMemoryService Service { get; }

        public async Task<ThreadMemoryRecord> CapturedRecordAsync()
        {
            var calls = this.Repository.ReceivedCalls()
                .Where(call => call.GetMethodInfo().Name == nameof(IThreadMemoryRepository.UpsertAsync))
                .ToList();
            Assert.Single(calls);
            await Task.CompletedTask;
            return (ThreadMemoryRecord)calls[0].GetArguments()[0]!;
        }
    }
}
