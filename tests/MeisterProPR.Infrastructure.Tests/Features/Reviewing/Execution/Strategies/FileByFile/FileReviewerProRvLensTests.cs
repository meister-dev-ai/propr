// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for the <c>prorv</c> review-pass lens in <see cref="FileReviewer" />: an eligible file is screened
///     against the ProRV catalog on the pass entry's own model (one ranking call); a match injects focused guidance
///     and runs the pass on any tier; no applicable check (or a deterministically ineligible file, or an unresolvable
///     model) skips the pass without a review call — never a tier resample.
/// </summary>
public sealed class FileReviewerProRvLensTests
{
    private readonly IAiReviewCore _aiCore = Substitute.For<IAiReviewCore>();
    private readonly IJobRepository _jobRepository = Substitute.For<IJobRepository>();
    private readonly IProtocolRecorder _recorder = Substitute.For<IProtocolRecorder>();
    private readonly IProRVPrefilter _prefilter = Substitute.For<IProRVPrefilter>();

    // Active lens observed on each aiCore review call, in order (null baseline, "prorv" for the lens pass).
    private readonly List<string?> _observedLenses = [];

    // Focused-guidance item count observed on each aiCore review call's context, in order.
    private readonly List<int> _observedGuidanceCounts = [];
    private int _aiCallCount;
    private ReviewFileResult? _persistedResult;

    public FileReviewerProRvLensTests()
    {
        this._recorder
            .BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(_ => Guid.NewGuid());

        this._jobRepository
            .When(r => r.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var result = ci.ArgAt<ReviewFileResult>(0);
                if (result.IsComplete)
                {
                    this._persistedResult = result;
                }
            });
    }

    private FileReviewer CreateReviewer(IAiRuntimeResolver? aiRuntimeResolver)
    {
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.ArgAt<ReviewSystemContext>(1);
                ctx.LoopMetrics = new ReviewLoopMetrics(0, null, null, 90, 100, 10, 1);
                this._observedLenses.Add(ctx.ActiveLens);
                this._observedGuidanceCounts.Add(ctx.PerFileHint?.FocusedReviewGuidance?.Count ?? 0);
                this._aiCallCount++;
                var filePath = ctx.PerFileHint?.FilePath ?? "Program.cs";
                // Distinct anchor per call so the union preserves each pass's finding through per-file filtering.
                var comment = new ReviewComment(filePath, 10 + (this._aiCallCount * 10), CommentSeverity.Warning, $"Concrete defect {this._aiCallCount}.");
                return new ReviewResult("summary", [comment]);
            });

        return new FileReviewer(
            this._aiCore,
            this._recorder,
            this._jobRepository,
            new AiReviewOptions(),
            NullLogger<FileByFileReviewOrchestrator>.Instance,
            null,
            null,
            null,
            null,
            aiRuntimeResolver,
            null,
            null,
            null,
            null,
            this._prefilter);
    }

    private void GivenPrefilterReturns(ProRVPrefilterStatus status, params ProRVRelevantItem[] items)
    {
        this._prefilter
            .RankRelevantItemsAsync(Arg.Any<ProRVPrefilterRequest>(), Arg.Any<IChatClient>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ProRVPrefilterResult(status, ci.ArgAt<ProRVPrefilterRequest>(0).FilePath, "csharp", items));
    }

    private static ProRVRelevantItem RelevantItem(string id)
    {
        return new ProRVRelevantItem(id, $"Title {id}", "short", "focused instruction", "reason", 90, "high", "high", []);
    }

    private static IAiRuntimeResolver ResolverForModel(string remoteModelId)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(Substitute.For<IChatClient>());
        runtime.Model.Returns(new AiConfiguredModelDto(Guid.NewGuid(), remoteModelId, remoteModelId, [AiOperationKind.Chat], [AiProtocolMode.Auto]));
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(runtime);
        return resolver;
    }

    private static ChangedFile FileForTier(FileComplexityTier tier, bool isBinary = false)
    {
        var changedLines = tier switch
        {
            FileComplexityTier.Low => 5,
            FileComplexityTier.Medium => 60,
            _ => 200,
        };
        var diff = new StringBuilder("@@ -1,1 +1,1 @@\n");
        for (var i = 0; i < changedLines; i++)
        {
            diff.Append("+ added line ").Append(i).Append('\n');
        }

        return new ChangedFile("Program.cs", ChangeType.Edit, "full content", diff.ToString(), isBinary);
    }

    private static (ReviewJob job, PullRequest pr) Fixture(ChangedFile file)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 32, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 32, 1, "PR", null, "feature/x", "main",
            new List<ChangedFile> { file }.AsReadOnly());
        return (job, pr);
    }

    private static ReviewSystemContext ProRvLensContext()
    {
        return new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            ModelId = "gpt-5.3-codex",
            ReviewPasses = [new ReviewPassSpec(Guid.NewGuid(), ReviewPassLens.ProRV)],
        };
    }

    [Fact]
    public async Task ProRvLens_ApplicableFile_RunsPassWithGuidance_AtAnyTier()
    {
        // Low tier: the prorv lens still runs when the catalog applies (any-tier gating).
        this.GivenPrefilterReturns(ProRVPrefilterStatus.Success, RelevantItem("cs/check-1"));
        var reviewer = this.CreateReviewer(ResolverForModel("prorv-model"));
        var file = FileForTier(FileComplexityTier.Low);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, ProRvLensContext(), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // Baseline pass + the prorv lens pass.
        await this._aiCore.Received(2).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        // The applicability ranking ran exactly once, on the lens pass's own model.
        await this._prefilter.Received(1).RankRelevantItemsAsync(
            Arg.Any<ProRVPrefilterRequest>(), Arg.Any<IChatClient>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        // The lens pass reviewed under ActiveLens=prorv with the ranked guidance injected into its context.
        Assert.Equal(new[] { null, ReviewPassLens.ProRV }, this._observedLenses.ToArray());
        Assert.Equal(0, this._observedGuidanceCounts[0]);
        Assert.Equal(1, this._observedGuidanceCounts[1]);
        // The lens finding carries the prorv provenance for the "Pass N · ProRV" rendering.
        Assert.NotNull(this._persistedResult);
        Assert.Contains(this._persistedResult!.Comments!, c => c.OriginPassLens == ReviewPassLens.ProRV);
    }

    [Fact]
    public async Task ProRvLens_NoApplicableChecks_SkipsPass_NoReviewCall()
    {
        // The ranking runs but finds nothing applicable → the pass is skipped without a review model call.
        this.GivenPrefilterReturns(ProRVPrefilterStatus.Success);
        var reviewer = this.CreateReviewer(ResolverForModel("prorv-model"));
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, ProRvLensContext(), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // Only the baseline review ran; the prorv pass contributed no review call.
        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        // The ranking (the gate) still ran once.
        await this._prefilter.Received(1).RankRelevantItemsAsync(
            Arg.Any<ProRVPrefilterRequest>(), Arg.Any<IChatClient>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.NotNull(this._persistedResult);
        Assert.DoesNotContain(this._persistedResult!.Comments!, c => c.OriginPassLens == ReviewPassLens.ProRV);
    }

    [Fact]
    public async Task ProRvLens_IneligibleFile_SkipsDeterministically_NoModelCall()
    {
        // A binary file (High tier, so it enters multi-pass planning) is deterministically ineligible: the prorv
        // pass is dropped before any model call — the ranking never runs.
        this.GivenPrefilterReturns(ProRVPrefilterStatus.Success, RelevantItem("cs/check-1"));
        var reviewer = this.CreateReviewer(ResolverForModel("prorv-model"));
        var file = FileForTier(FileComplexityTier.High, isBinary: true);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, ProRvLensContext(), null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        await this._prefilter.DidNotReceive().RankRelevantItemsAsync(
            Arg.Any<ProRVPrefilterRequest>(), Arg.Any<IChatClient>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProRvLens_UnresolvablePassModel_SkipsThatPass_NeverRanks()
    {
        // The lens pass's configured model cannot be resolved → the pass is skipped (never a tier resample) and the
        // ranking never runs; the baseline still completes.
        this.GivenPrefilterReturns(ProRVPrefilterStatus.Success, RelevantItem("cs/check-1"));
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<IResolvedAiChatRuntime?>(_ => throw new InvalidOperationException("model deleted"));
        var reviewer = this.CreateReviewer(resolver);
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, ProRvLensContext(), null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        await this._prefilter.DidNotReceive().RankRelevantItemsAsync(
            Arg.Any<ProRVPrefilterRequest>(), Arg.Any<IChatClient>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProRvLens_NotConfigured_RunsSinglePass()
    {
        // No prorv entry on the pass list → no prorv path is exercised (single baseline pass).
        this.GivenPrefilterReturns(ProRVPrefilterStatus.Success, RelevantItem("cs/check-1"));
        var reviewer = this.CreateReviewer(ResolverForModel("prorv-model"));
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);
        var context = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            ModelId = "gpt-5.3-codex",
            ReviewPasses = [],
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, context, null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        await this._prefilter.DidNotReceive().RankRelevantItemsAsync(
            Arg.Any<ProRVPrefilterRequest>(), Arg.Any<IChatClient>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoProRvPasses_DifferentModels_EachRanksOnItsOwnModel()
    {
        // A client may configure two prorv passes bound to distinct models. The per-(file,model,catalog) cache must
        // not collapse them: each pass ranks on its OWN model, so the applicability call runs once per model.
        this.GivenPrefilterReturns(ProRVPrefilterStatus.Success, RelevantItem("cs/check-1"));

        // Resolve each configured model to a runtime whose remote model id is the configured id — so the two passes
        // carry distinct pass-model ids (distinct cache keys).
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var configuredId = ci.ArgAt<Guid>(1).ToString();
                var runtime = Substitute.For<IResolvedAiChatRuntime>();
                runtime.ChatClient.Returns(Substitute.For<IChatClient>());
                runtime.Model.Returns(new AiConfiguredModelDto(Guid.NewGuid(), configuredId, configuredId, [AiOperationKind.Chat], [AiProtocolMode.Auto]));
                return runtime;
            });

        var reviewer = this.CreateReviewer(resolver);
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);
        var context = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            ModelId = "gpt-5.3-codex",
            ReviewPasses = [new ReviewPassSpec(Guid.NewGuid(), ReviewPassLens.ProRV), new ReviewPassSpec(Guid.NewGuid(), ReviewPassLens.ProRV)],
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, context, null, Substitute.For<IChatClient>(), CancellationToken.None);

        // Baseline + two prorv passes.
        await this._aiCore.Received(3).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        // The ranking ran once per model — the second pass was NOT served the first pass's cached ranking.
        await this._prefilter.Received(2).RankRelevantItemsAsync(
            Arg.Any<ProRVPrefilterRequest>(), Arg.Any<IChatClient>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
