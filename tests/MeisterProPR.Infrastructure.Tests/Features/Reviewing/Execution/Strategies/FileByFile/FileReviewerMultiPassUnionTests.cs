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
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for multi-pass union generation in <see cref="FileReviewer" />: it runs additional independent
///     passes only for in-scope tiers, unions every distinct finding before the single persistence write, records
///     per-pass provenance, and — when the client has not opted in — behaves identically to a single-pass review.
/// </summary>
public sealed class FileReviewerMultiPassUnionTests
{
    // Each AI call returns a distinct concrete finding so the union across passes is observable and nothing
    // is dropped by the per-file filters (Warning severity, no hedge/vague/info phrasing).
    private static readonly IReadOnlyList<ReviewComment> PassComments =
    [
        new("Program.cs", 10, CommentSeverity.Warning, "Null reference risk when config is missing."),
        new("Program.cs", 40, CommentSeverity.Warning, "Resource leak: the stream is never disposed."),
        new("Program.cs", 70, CommentSeverity.Warning, "Off-by-one error in the loop bound."),
        new("Program.cs", 90, CommentSeverity.Warning, "Unchecked cast can throw at runtime."),
    ];

    private static readonly string[] NullRefLeakOffByOneMessages =
    [
        "Null reference risk when config is missing.",
        "Resource leak: the stream is never disposed.",
        "Off-by-one error in the loop bound.",
    ];

    private static readonly string?[] TierThenGpt54ThenGpt54Mini =
        ["gpt-5.3-codex", "gpt-5.4", "gpt-5.4-mini"];

    private static readonly string?[] Gpt54ThenCodexTwice =
        ["gpt-5.4", "gpt-5.3-codex", "gpt-5.3-codex"];

    private static readonly string?[] CodexThenGpt54Twice =
        ["gpt-5.3-codex", "gpt-5.4", "gpt-5.4"];

    private readonly IAiReviewCore _aiCore = Substitute.For<IAiReviewCore>();

    // Reason string recorded for each opened protocol pass, in call order.
    private readonly List<string?> _beginReasons = [];
    private readonly IJobRepository _jobRepository = Substitute.For<IJobRepository>();

    // Model id observed on each AI call's context, in call order (baseline pass first, then resample passes).
    private readonly List<string?> _observedModelIds = [];
    private readonly IProtocolRecorder _recorder = Substitute.For<IProtocolRecorder>();
    private int _aiCallCount;
    private ReviewFileResult? _persistedResult;

    public FileReviewerMultiPassUnionTests()
    {
        this._recorder
            .BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(ci =>
            {
                this._beginReasons.Add(ci.ArgAt<string?>(8));
                return Guid.NewGuid();
            });

        // Capture the per-file result at the single completion write so tests can inspect the unioned comments.
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

    private FileReviewer CreateReviewer(IAiRuntimeResolver? aiRuntimeResolver = null)
    {
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.ArgAt<ReviewSystemContext>(1);
                ctx.LoopMetrics = new ReviewLoopMetrics(0, null, null, 90, 100, 10, 1);
                this._observedModelIds.Add(ctx.ModelId);
                var comment = PassComments[Math.Min(this._aiCallCount, PassComments.Count - 1)];
                this._aiCallCount++;
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
            null);
    }

    // Minimal chat-capable configured-model DTO carrying the RemoteModelId the review core selects on.
    private static AiConfiguredModelDto ConfiguredModel(string remoteModelId)
    {
        return new AiConfiguredModelDto(
            Guid.NewGuid(),
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto]);
    }

    // Builds a changed file whose size heuristic lands on the requested tier (<=30 Low, <=150 Medium, else High).
    private static ChangedFile FileForTier(FileComplexityTier tier)
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

        return new ChangedFile("Program.cs", ChangeType.Edit, "full content", diff.ToString());
    }

    private static (ReviewJob job, PullRequest pr) Fixture(ChangedFile file)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 32, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 32, 1, "PR", null, "feature/x", "main",
            new List<ChangedFile> { file }.AsReadOnly());
        return (job, pr);
    }

    private static ReviewSystemContext MultiPassContext(int passCount)
    {
        return new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            MultiPassUnionPassCount = passCount,
            // Supply an eval-harness resample model so the fan-out runs over the tier connection (the production
            // per-client review-pass path is covered separately).
            MultiPassDiversity = new MultiPassDiversity(DefaultModel: "gpt-5.4"),
        };
    }

    // Production multi-pass context: no eval pass count, the extra passes come from the ordered review-pass list.
    private static ReviewSystemContext ProductionContext(string tierModelId, params Guid[] passModelIds)
    {
        return new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            ModelId = tierModelId,
            ReviewPasses = passModelIds,
        };
    }

    // A resolved chat runtime whose configured model carries the given remote id; the observed context ModelId on
    // each resample pass is this value, so tests can assert which model each pass ran on.
    private static IResolvedAiChatRuntime RuntimeForModel(string remoteModelId)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(Substitute.For<IChatClient>());
        runtime.Model.Returns(ConfiguredModel(remoteModelId));
        return runtime;
    }

    [Fact]
    public async Task FlagOff_RunsSinglePass_ByteIdenticalToToday()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Medium);
        var (job, pr) = Fixture(file);

        // Flag off — even a Medium file must not fan out.
        var baseContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = false,
            MultiPassUnionPassCount = 3,
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, baseContext, null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        Assert.NotNull(this._persistedResult);
        var comments = this._persistedResult!.Comments!;
        Assert.Single(comments);
        // No union stamping when the flag is off — provenance is exactly as a single-pass review produces.
        Assert.Null(comments[0].OriginPassKind);
        await this._recorder.DidNotReceive().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LowTier_RunsSinglePass_EvenWhenEnabled()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Low);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, MultiPassContext(3), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // Tier binding: multi-pass union runs only for Medium/High tiers; a Low-tier file gets exactly one pass.
        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        await this._recorder.DidNotReceive().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(FileComplexityTier.Medium)]
    [InlineData(FileComplexityTier.High)]
    public async Task InScopeTier_FansOutKTimes_AndUnionsDistinctFindings(FileComplexityTier tier)
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(tier);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, MultiPassContext(3), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // k=3 fan-out: the baseline pass plus two resamples.
        await this._aiCore.Received(3).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());

        // Union preserves every distinct finding — the three passes each contributed one.
        Assert.NotNull(this._persistedResult);
        var comments = this._persistedResult!.Comments!;
        Assert.Equal(3, comments.Count);
        Assert.Equal(NullRefLeakOffByOneMessages, comments.Select(c => c.Message).ToArray());
    }

    [Fact]
    public async Task InScopeTier_RecordsProvenancePerPass()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, MultiPassContext(3), null, Substitute.For<IChatClient>(), CancellationToken.None);

        Assert.NotNull(this._persistedResult);
        var comments = this._persistedResult!.Comments!;

        // The baseline pass keeps its natural (unstamped) origin; the two resamples are tagged as union passes.
        Assert.Null(comments[0].OriginPassKind);
        Assert.Equal(nameof(ReviewPassKind.MultiPassUnion), comments[1].OriginPassKind);
        Assert.Equal(nameof(ReviewPassKind.MultiPassUnion), comments[2].OriginPassKind);

        // Each resample finding also carries the 1-based pass index (baseline = pass 1, so resamples are 2 and 3),
        // which the frontend renders as "Pass N". The baseline finding has no numbered origin.
        Assert.Null(comments[0].OriginPassIndex);
        Assert.Equal(2, comments[1].OriginPassIndex);
        Assert.Equal(3, comments[2].OriginPassIndex);

        // A single completion event records the per-pass catch counts so funnel attribution keeps working.
        await this._recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InScopeTier_RecordsPassIndexInProtocolReason()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, MultiPassContext(3), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // Each additional union pass opens a protocol pass whose reason carries the 1-based pass index in the
        // "pass #N" form the frontend parses (/pass #(\d+)/) to render "Pass N". k=3 => resample passes #2 and #3.
        var unionReasons = this._beginReasons
            .Where(reason => reason is not null && reason.Contains("multi-pass union", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, unionReasons.Count);
        Assert.All(unionReasons, reason => Assert.Matches(@"pass #\d+", reason!));
        Assert.Contains(unionReasons, reason => reason!.Contains("pass #2", StringComparison.Ordinal));
        Assert.Contains(unionReasons, reason => reason!.Contains("pass #3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resampling_ResamplePassesUseDefaultModel_BaselineKeepsTierModel()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Medium);
        var (job, pr) = Fixture(file);

        var baseContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            MultiPassUnionPassCount = 3,
            // The file's tier model; resample passes must switch off this to the diversity default model.
            ModelId = "gpt-5.3-codex",
            MultiPassDiversity = new MultiPassDiversity(DefaultModel: "gpt-5.4"),
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, baseContext, null, Substitute.For<IChatClient>(), CancellationToken.None);

        // k=3: the baseline pass plus two resamples, observed in call order.
        Assert.Equal(3, this._observedModelIds.Count);
        // The baseline pass stays on the file's tier model.
        Assert.Equal("gpt-5.3-codex", this._observedModelIds[0]);
        // The resample passes run the diversity default model — the recall lever.
        Assert.Equal("gpt-5.4", this._observedModelIds[1]);
        Assert.Equal("gpt-5.4", this._observedModelIds[2]);
    }

    [Fact]
    public async Task Production_FlagOnEmptyPassList_SkipsExtraPasses_SinglePass()
    {
        // Flag on, but the client configured no review passes: exactly one pass runs and a skip event is recorded
        // (never a tier-model resample). No completion event.
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Medium);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, ProductionContext("gpt-5.3-codex"), null, Substitute.For<IChatClient>(), CancellationToken.None);

        Assert.Single(this._observedModelIds);
        Assert.Equal("gpt-5.3-codex", this._observedModelIds[0]);
        await this._recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionSkipped,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await this._recorder.DidNotReceive().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Production_ResolvesEachPassFromReviewPassList_BaselineKeepsTierModel()
    {
        // Two configured review-pass models: the baseline keeps the tier model, and each additional pass runs on the
        // model resolved for its own configured-model id — the union then preserves every distinct finding.
        var firstPassModel = Guid.NewGuid();
        var secondPassModel = Guid.NewGuid();
        // Build the runtimes first: configuring a substitute inside another substitute's Returns(...) corrupts
        // NSubstitute's last-call tracking.
        var firstRuntime = RuntimeForModel("model-a");
        var secondRuntime = RuntimeForModel("model-b");
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), firstPassModel, Arg.Any<CancellationToken>())
            .Returns(firstRuntime);
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), secondPassModel, Arg.Any<CancellationToken>())
            .Returns(secondRuntime);

        var reviewer = this.CreateReviewer(resolver);
        var file = FileForTier(FileComplexityTier.Medium);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, ProductionContext("gpt-5.3-codex", firstPassModel, secondPassModel), null,
            Substitute.For<IChatClient>(), CancellationToken.None);

        // Baseline + two passes, each on its own resolved model.
        Assert.Equal(3, this._observedModelIds.Count);
        Assert.Equal("gpt-5.3-codex", this._observedModelIds[0]);
        Assert.Equal("model-a", this._observedModelIds[1]);
        Assert.Equal("model-b", this._observedModelIds[2]);

        Assert.NotNull(this._persistedResult);
        var comments = this._persistedResult!.Comments!;
        Assert.Equal(3, comments.Count);
        Assert.Equal(NullRefLeakOffByOneMessages, comments.Select(c => c.Message).ToArray());
        await this._recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Production_UnresolvablePassModel_SkipsThatPassRunsOthers()
    {
        // The first configured pass model cannot be resolved (e.g. deleted): that pass is skipped with a per-pass
        // trace note, the remaining pass still runs, and the baseline is never resampled on the tier model.
        var missingPassModel = Guid.NewGuid();
        var resolvablePassModel = Guid.NewGuid();
        var resolvableRuntime = RuntimeForModel("model-b");
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), missingPassModel, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IResolvedAiChatRuntime>(new InvalidOperationException("model deleted")));
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), resolvablePassModel, Arg.Any<CancellationToken>())
            .Returns(resolvableRuntime);

        var reviewer = this.CreateReviewer(resolver);
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, ProductionContext("gpt-5.3-codex", missingPassModel, resolvablePassModel), null,
            Substitute.For<IChatClient>(), CancellationToken.None);

        // Baseline + only the resolvable pass — the unresolvable one is dropped.
        Assert.Equal(2, this._observedModelIds.Count);
        Assert.Equal("gpt-5.3-codex", this._observedModelIds[0]);
        Assert.Equal("model-b", this._observedModelIds[1]);

        await this._recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionPassSkipped,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await this._recorder.Received(1).RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.MultiPassUnionCompleted,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrossModel_RoutesResamplePassesToArmModels_BaselineKeepsTierModel()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Medium);
        var (job, pr) = Fixture(file);

        var baseContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            MultiPassUnionPassCount = 3,
            ModelId = "gpt-5.3-codex",
            MultiPassDiversity = new MultiPassDiversity(
                MultiPassDiversityMode.CrossModel,
                Arms:
                [
                    new MultiPassArm("gpt-5.4", "gpt-5.4"),
                    new MultiPassArm("mini", "gpt-5.4-mini"),
                ]),
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, baseContext, null, Substitute.For<IChatClient>(), CancellationToken.None);

        // The baseline pass stays on the file's tier model; the resample passes span the declared arm models.
        Assert.Equal(TierThenGpt54ThenGpt54Mini, this._observedModelIds.ToArray());
    }

    [Fact]
    public async Task CrossModel_CyclesArmsWhenFewerThanResamplePasses()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        var baseContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            MultiPassUnionPassCount = 3,
            ModelId = "gpt-5.4",
            // One arm, two resample passes — the arm model is reused for both.
            MultiPassDiversity = new MultiPassDiversity(
                MultiPassDiversityMode.CrossModel,
                Arms: [new MultiPassArm("codex", "gpt-5.3-codex")]),
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, baseContext, null, Substitute.For<IChatClient>(), CancellationToken.None);

        Assert.Equal(Gpt54ThenCodexTwice, this._observedModelIds.ToArray());
    }

    [Fact]
    public async Task CrossModel_WithoutArms_FallsBackToDefaultModel()
    {
        var reviewer = this.CreateReviewer();
        var file = FileForTier(FileComplexityTier.Medium);
        var (job, pr) = Fixture(file);

        var baseContext = new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            MultiPassUnionPassCount = 3,
            ModelId = "gpt-5.3-codex",
            // Cross-model with no arms degrades to the diversity default model on the resample passes.
            MultiPassDiversity = new MultiPassDiversity(MultiPassDiversityMode.CrossModel, "gpt-5.4", Arms: null),
        };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, baseContext, null, Substitute.For<IChatClient>(), CancellationToken.None);

        Assert.Equal(CodexThenGpt54Twice, this._observedModelIds.ToArray());
    }
}
