// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for the <c>inventory</c> review-pass lens in <see cref="FileReviewer" />: a text file with a diff runs
///     the pass on any tier; the pass context carries no repository tools, so the model judges the diff text alone;
///     a binary file skips the pass. Filtering of the inventory happens in the downstream union and verification
///     stages, so the pass itself only has to contribute candidates. Local verification of union-pass findings, from
///     inventory and ordinary resample passes alike, uses the baseline file context's tools and client, so the
///     evidence-backed verifier can confirm a claim the pass could not investigate itself.
/// </summary>
public sealed class FileReviewerInventoryLensTests
{
    private readonly IAiReviewCore _aiCore = Substitute.For<IAiReviewCore>();
    private readonly IJobRepository _jobRepository = Substitute.For<IJobRepository>();
    private readonly IProtocolRecorder _recorder = Substitute.For<IProtocolRecorder>();

    // Active lens and tool availability observed on each aiCore review call, in call order.
    private readonly List<string?> _observedLenses = [];
    private readonly List<bool> _observedHasTools = [];
    private int _aiCallCount;
    private ReviewFileResult? _persistedResult;

    public FileReviewerInventoryLensTests()
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

    private FileReviewer CreateReviewer(
        IAiRuntimeResolver? aiRuntimeResolver,
        LocalReviewVerificationExecutor? localReviewVerificationExecutor = null)
    {
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.ArgAt<ReviewSystemContext>(1);
                ctx.LoopMetrics = new ReviewLoopMetrics(0, null, null, 90, 100, 10, 1);
                this._observedLenses.Add(ctx.ActiveLens);
                this._observedHasTools.Add(ctx.ReviewTools is not null);
                this._aiCallCount++;
                var filePath = ctx.PerFileHint?.FilePath ?? "Program.cs";
                // Distinct anchor per call so the union preserves each pass's finding through per-file filtering.
                var comment = new ReviewComment(filePath, 10 + (this._aiCallCount * 10), CommentSeverity.Warning, $"Concrete defect {this._aiCallCount}.");
                return new ReviewResult("summary", [comment]);
            });

        return new FileReviewer(
            null,
            this._aiCore,
            this._recorder,
            this._jobRepository,
            new AiReviewOptions(),
            NullLogger<FileByFileReviewOrchestrator>.Instance,
            null,
            null,
            aiRuntimeResolver,
            null,
            null,
            localReviewVerificationExecutor,
            null);
    }

    private static IAiRuntimeResolver ResolverForModel(string remoteModelId)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(Substitute.For<IChatClient>());
        runtime.Model.Returns(
            new AiConfiguredModelDto(Guid.NewGuid(), remoteModelId, remoteModelId, [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]));
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(runtime);
        return resolver;
    }

    // A resolver that also binds the low-effort purpose to the given judge client, so the evidence verifier judges on
    // the low-effort model and never on the pass model.
    private static IAiRuntimeResolver ResolverWithLowEffortJudge(string passModelId, IChatClient judge)
    {
        var resolver = ResolverForModel(passModelId);
        var verificationRuntime = Substitute.For<IResolvedAiChatRuntime>();
        verificationRuntime.ChatClient.Returns(judge);
        verificationRuntime.Model.Returns(
            new AiConfiguredModelDto(Guid.NewGuid(), "low-effort-model", "low-effort-model", [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]));
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewLowEffort, Arg.Any<CancellationToken>())
            .Returns(verificationRuntime);
        return resolver;
    }

    // Every comment yields one claim that needs symbol evidence. The deterministic verifier can only withhold such a
    // claim, so whether the finding survives depends on the evidence-backed verifier.
    private LocalReviewVerificationExecutor CreateEvidenceVerificationExecutor()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(ci =>
            {
                var finding = ci.Arg<CandidateReviewFinding>();
                return
                [
                    new ClaimDescriptor(
                        $"claim-{finding.FindingId}",
                        finding.FindingId,
                        ClaimDescriptor.LocalStage,
                        CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
                        finding.Message,
                        finding.Severity,
                        ClaimDescriptor.DeterministicOnlyMode,
                        ClaimDescriptor.ApiOrSymbolUsageFamily,
                        subjectIdentifier: "lookup",
                        anchorFilePath: finding.FilePath,
                        anchorLineNumber: finding.LineNumber,
                        requiresSymbolEvidence: true),
                ];
            });

        return new LocalReviewVerificationExecutor(
            extractor,
            new CompositeReviewFindingVerifier(new DeterministicLocalReviewVerifier(), new EvidenceBackedReviewVerifier()),
            this._recorder);
    }

    // The production claim extractor and verifiers. A plain review comment is a CodeContract claim, which the
    // deterministic verifier publishes on its own unless the finding is marked as needing evidence.
    private LocalReviewVerificationExecutor CreateProductionVerificationExecutor()
    {
        return new LocalReviewVerificationExecutor(
            new DeterministicReviewClaimExtractor(),
            new CompositeReviewFindingVerifier(new DeterministicLocalReviewVerifier(), new EvidenceBackedReviewVerifier()),
            this._recorder);
    }

    // Records the serialized outcome of every local verification decision, in call order.
    private List<string> CaptureLocalDecisions()
    {
        var decisions = new List<string>();
        this._recorder
            .When(r => r.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.VerificationLocalDecision,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci => decisions.Add(ci.ArgAt<string?>(3) ?? string.Empty));
        return decisions;
    }

    private static IChatClient JudgeReturning(string verdictJson)
    {
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, verdictJson)));
        return judge;
    }

    private static IReviewContextTools ToolsWithAnchorSource()
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("public string Lookup(string? key)\n{\n    return _map[key].ToString();\n}");
        return tools;
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

    private static ReviewSystemContext InventoryLensContext(IReviewContextTools? tools = null)
    {
        return UnionContext(ReviewPassLens.Inventory, tools);
    }

    private static ReviewSystemContext UnionContext(string? passLens, IReviewContextTools? tools)
    {
        return new ReviewSystemContext(null, [], tools)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            ModelId = "gpt-5.3-codex",
            ReviewPasses = [new ReviewPassSpec(Guid.NewGuid(), passLens)],
        };
    }

    // A resolver whose configured pass model runs on the given pass client and which has no binding for any purpose,
    // so neither a tier runtime nor a review-verification runtime resolves.
    private static IAiRuntimeResolver ResolverWithoutVerificationBinding(IChatClient passClient, string passModelId)
    {
        var passRuntime = Substitute.For<IResolvedAiChatRuntime>();
        passRuntime.ChatClient.Returns(passClient);
        passRuntime.Model.Returns(
            new AiConfiguredModelDto(Guid.NewGuid(), passModelId, passModelId, [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]));
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(passRuntime);
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>())
            .Returns<IResolvedAiChatRuntime>(_ => throw new InvalidOperationException("no binding"));
        return resolver;
    }

    [Fact]
    public async Task BaselineAndInventoryFindings_JudgeReceivesPullRequestIntentAndAnchorHunk()
    {
        // The judge weighs every finding against the pull request description, the linked work items collected for
        // the review, and the diff hunk at the anchor, for baseline findings and union-pass findings alike.
        var userMessages = new List<string>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => userMessages.Add(messages.Last().Text)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"visible at line 3\"}")));
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateEvidenceVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = IntentFixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        Assert.Equal(2, userMessages.Count);
        Assert.All(
            userMessages,
            message =>
            {
                Assert.Contains("Committed offsets skip unprocessable messages.", message, StringComparison.Ordinal);
                Assert.Contains("User Story #42: Consumer must not stall", message, StringComparison.Ordinal);
                Assert.Contains("@@ -1,1 +1,1 @@", message, StringComparison.Ordinal);
            });
    }

    [Theory]
    [InlineData("{\"verdict\":\"intended\",\"reason\":\"the description states the skip\"}", "intended", false)]
    [InlineData(
        "{\"verdict\":\"intended_contradicted\",\"reason\":\"failed offsets are committed\",\"contradicts\":\"linked work item #42\"}",
        "intended_contradicted",
        true)]
    [InlineData("{\"verdict\":\"not_actionable\",\"reason\":\"no reachable caller passes zero\"}", "not_actionable", false)]
    public async Task BaselineAndInventoryFindings_GetTheSameDispositionForAJudgeVerdict_AndRecordTheVerdictKind(
        string verdictJson,
        string expectedVerdict,
        bool expectPublished)
    {
        var decisions = this.CaptureLocalDecisions();
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", JudgeReturning(verdictJson)),
            this.CreateEvidenceVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = IntentFixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        Assert.Equal(2, decisions.Count);
        Assert.All(
            decisions,
            decision =>
            {
                using var document = JsonDocument.Parse(decision);
                Assert.Equal(expectedVerdict, document.RootElement.GetProperty("judgeVerdict").GetString());
            });
        Assert.NotNull(this._persistedResult);
        Assert.Equal(expectPublished ? 2 : 0, this._persistedResult!.Comments?.Count ?? 0);
    }

    private static (ReviewJob job, PullRequest pr) IntentFixture(ChangedFile file)
    {
        var (job, pr) = Fixture(file);
        return (job, pr with
        {
            Description = "Committed offsets skip unprocessable messages.",
            LinkedItems = [new LinkedItem("42", "User Story", "Consumer must not stall", "Failed messages are retried.", null, [])],
        });
    }

    [Fact]
    public async Task InventoryLens_TextDiffFile_RunsPass_AtAnyTier()
    {
        // Low tier is outside the ordinary Medium/High union gating; the inventory lens runs there anyway because
        // its eligibility is only "text file with a diff".
        var reviewer = this.CreateReviewer(ResolverForModel("inventory-model"));
        var file = FileForTier(FileComplexityTier.Low);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, InventoryLensContext(), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // Baseline pass + the inventory lens pass.
        await this._aiCore.Received(2).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        Assert.Equal(new[] { null, ReviewPassLens.Inventory }, this._observedLenses.ToArray());
        // The lens finding carries the inventory provenance for the "Pass N · Inventory" rendering.
        Assert.NotNull(this._persistedResult);
        Assert.Contains(this._persistedResult!.Comments!, c => c.OriginPassLens == ReviewPassLens.Inventory);
    }

    [Fact]
    public async Task InventoryLens_PassContextCarriesNoTools_BaselineKeepsThem()
    {
        // The base context has repository tools; the inventory pass context must not, because the pass judges the
        // diff text alone and any navigation would re-introduce the investigation behavior the lens exists to avoid.
        var reviewer = this.CreateReviewer(ResolverForModel("inventory-model"));
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(Substitute.For<IReviewContextTools>()), null, Substitute.For<IChatClient>(), CancellationToken.None);

        Assert.Equal(2, this._observedHasTools.Count);
        Assert.True(this._observedHasTools[0]);
        Assert.False(this._observedHasTools[1]);
        Assert.Equal(ReviewPassLens.Inventory, this._observedLenses[1]);
    }

    [Fact]
    public async Task InventoryLens_BinaryFile_SkipsPass()
    {
        // A binary file has no reviewable diff text, so the lens contributes nothing and a Low-tier file does not
        // fan out at all.
        var reviewer = this.CreateReviewer(ResolverForModel("inventory-model"));
        var file = FileForTier(FileComplexityTier.Low, isBinary: true);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, InventoryLensContext(), null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        Assert.Equal(new string?[] { null }, this._observedLenses.ToArray());
    }

    [Fact]
    public async Task InventoryLens_FindingConfirmedByEvidenceVerifier_SurvivesUnion()
    {
        // The inventory pass reviews without tools, but its finding needs symbol evidence. Verification must read the
        // anchor code through the baseline context's tools and let the judge confirm the claim, so the finding reaches
        // the persisted per-file result that synthesis reads.
        var judge = JudgeReturning("{\"verdict\":\"confirmed\",\"reason\":\"line 3 dereferences a possibly-null lookup\"}");
        var tools = ToolsWithAnchorSource();
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateEvidenceVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(job, pr, file, 1, 1, InventoryLensContext(tools), null, Substitute.For<IChatClient>(), CancellationToken.None);

        // The inventory review call itself still ran without tools.
        Assert.Equal(new[] { true, false }, this._observedHasTools.ToArray());
        // One judge call per pass: the baseline finding and the inventory finding were both judged on the low-effort
        // model.
        await judge.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.ModelId == "low-effort-model"),
            Arg.Any<CancellationToken>());
        await tools.Received(2).GetFileContentAsync("Program.cs", "feature/x", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.NotNull(this._persistedResult);
        Assert.Contains(this._persistedResult!.Comments!, c => c.OriginPassLens == ReviewPassLens.Inventory);
    }

    [Fact]
    public async Task InventoryLens_NoVerificationModelConfigured_JudgesOnBaselineClient()
    {
        // Without a low-effort binding the judge falls back to the baseline file context's client, as for
        // baseline findings, and never to the client of the inventory pass model.
        var baselineClient = JudgeReturning("{\"verdict\":\"confirmed\",\"reason\":\"line 3 dereferences a possibly-null lookup\"}");
        var passClient = Substitute.For<IChatClient>();
        var reviewer = this.CreateReviewer(
            ResolverWithoutVerificationBinding(passClient, "inventory-model"),
            this.CreateEvidenceVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, baselineClient,
            CancellationToken.None);

        await baselineClient.Received(2).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await passClient.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
        Assert.NotNull(this._persistedResult);
        Assert.Contains(this._persistedResult!.Comments!, c => c.OriginPassLens == ReviewPassLens.Inventory);
    }

    [Fact]
    public async Task OrdinaryResamplePass_NoVerificationModelConfigured_JudgesOnBaselineClient()
    {
        // An ordinary resample pass keeps the repository tools and reviews on its own pass client. Without a
        // low-effort binding its findings are still judged on the baseline client, so verification never
        // runs on the pass model.
        var baselineClient = JudgeReturning("{\"verdict\":\"confirmed\",\"reason\":\"line 3 dereferences a possibly-null lookup\"}");
        var passClient = JudgeReturning("{\"verdict\":\"confirmed\",\"reason\":\"confirmed\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithoutVerificationBinding(passClient, "resample-model"),
            this.CreateEvidenceVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, UnionContext(null, ToolsWithAnchorSource()), null, baselineClient,
            CancellationToken.None);

        // Both passes reviewed with tools and without a lens.
        Assert.Equal(new[] { true, true }, this._observedHasTools.ToArray());
        Assert.Equal(new string?[] { null, null }, this._observedLenses.ToArray());
        await baselineClient.Received(2).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await passClient.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
        Assert.NotNull(this._persistedResult);
        Assert.Equal(2, this._persistedResult!.Comments!.Count);
    }

    [Fact]
    public async Task InventoryLens_FindingNotConfirmedByEvidenceVerifier_StaysWithheld()
    {
        // The evidence verifier ran with the baseline tools but did not confirm the claim, so the inventory finding
        // keeps its withheld disposition and does not enter the union.
        var judge = JudgeReturning("{\"verdict\":\"not_confirmed\",\"reason\":\"key is checked at line 1\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateEvidenceVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        await judge.Received(2).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.NotNull(this._persistedResult);
        Assert.DoesNotContain(this._persistedResult!.Comments ?? [], c => c.OriginPassLens == ReviewPassLens.Inventory);
    }

    [Fact]
    public async Task InventoryLens_UnionCompletedEvent_RecordsCountsBeforeAndAfterPassPipeline()
    {
        // Each pass returns one comment from the review core, and verification withholds both because the judge does
        // not confirm. The trace records the count before the per-file pipeline next to the count after it, so the
        // loss inside a pass is visible.
        string? unionOutput = null;
        await this._recorder.RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(),
            Arg.Is(ReviewProtocolEventNames.MultiPassUnionCompleted),
            Arg.Any<string?>(),
            Arg.Do<string?>(output => unionOutput = output),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        var judge = JudgeReturning("{\"verdict\":\"not_confirmed\",\"reason\":\"not present\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateEvidenceVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        Assert.NotNull(unionOutput);
        using var document = JsonDocument.Parse(unionOutput!);
        var root = document.RootElement;
        Assert.Equal(new[] { 1, 1 }, root.GetProperty("perPassPreFilterCounts").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(new[] { 0, 0 }, root.GetProperty("perPassCatchCounts").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public async Task BaselineAndInventoryCodeContractFindings_AreBothJudgedBeforeUnion()
    {
        // A plain comment is a CodeContract claim. Every finding needs evidence, so the baseline finding and the
        // inventory finding both reach the judge before the union.
        var decisions = this.CaptureLocalDecisions();
        var judge = JudgeReturning("{\"verdict\":\"confirmed\",\"reason\":\"line 3 dereferences a possibly-null lookup\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateProductionVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        await judge.Received(2).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision => Assert.Contains(VerificationOutcome.AiMicroVerifierEvaluator, decision, StringComparison.Ordinal));
        Assert.NotNull(this._persistedResult);
        Assert.Equal(2, this._persistedResult!.Comments!.Count);
        Assert.Contains(this._persistedResult.Comments!, c => c.OriginPassLens == ReviewPassLens.Inventory);
    }

    [Fact]
    public async Task BaselineAndInventoryCodeContractFindingsNotConfirmed_AreBothWithheld()
    {
        var judge = JudgeReturning("{\"verdict\":\"not_confirmed\",\"reason\":\"key is checked at line 1\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateProductionVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        await judge.Received(2).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.NotNull(this._persistedResult);
        Assert.Empty(this._persistedResult!.Comments ?? []);
    }

    [Fact]
    public async Task JudgeReceivesTheReadsOfThePassThatProducedTheFinding()
    {
        // The baseline pass read src/Map.cs, the inventory pass read nothing. The judge of the baseline finding gets an
        // excerpt of that read; the judge of the inventory finding does not.
        var userMessages = new List<string>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => userMessages.Add(messages.Last().Text)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"verdict\":\"confirmed\",\"reason\":\"visible\"}")));
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateProductionVerificationExecutor());
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.ArgAt<ReviewSystemContext>(1);
                ctx.LoopMetrics = new ReviewLoopMetrics(0, null, null, 90, 100, 10, 1);
                this._aiCallCount++;
                if (ctx.ReviewTools is not null)
                {
                    ctx.ReviewerFileReads = [new ReviewerFileRead("src/Map.cs", 1, 2)];
                }

                return new ReviewResult(
                    "summary",
                    [
                        new ReviewComment(
                            "Program.cs", 10 + (this._aiCallCount * 10), CommentSeverity.Warning,
                            $"`LookupEntry` throws for a missing key ({this._aiCallCount}).")
                    ]);
            });
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("var entry = LookupEntry(key);");
        tools.GetFileContentAsync("src/Map.cs", Arg.Any<string>(), 1, 2, Arg.Any<CancellationToken>())
            .Returns("public Entry LookupEntry(string key)\n    => _map[key];");
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(tools), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        Assert.Equal(2, userMessages.Count);
        var baseline = Assert.Single(userMessages, message => message.Contains("(1).", StringComparison.Ordinal));
        Assert.Contains("--- src/Map.cs, lines 1-2 ---", baseline, StringComparison.Ordinal);
        Assert.Contains("public Entry LookupEntry(string key)", baseline, StringComparison.Ordinal);
        var inventory = Assert.Single(userMessages, message => message.Contains("(2).", StringComparison.Ordinal));
        Assert.DoesNotContain("src/Map.cs", inventory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsufficientContext_PublishesTheBaselineFinding_AndWithholdsTheInventoryFinding()
    {
        // The baseline pass reviewed with repository tools and the inventory pass without. The judge answers that the
        // code it needs is missing for both findings, so only the baseline finding is published.
        var decisions = this.CaptureLocalDecisions();
        var judge = JudgeReturning("{\"verdict\":\"insufficient_context\",\"reason\":\"the body of createEvent is not shown\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("inventory-model", judge),
            this.CreateProductionVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, InventoryLensContext(ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        Assert.Equal(2, decisions.Count);
        Assert.All(
            decisions,
            decision =>
            {
                using var document = JsonDocument.Parse(decision);
                Assert.Equal(EvidenceJudgeVerdicts.InsufficientContext, document.RootElement.GetProperty("judgeVerdict").GetString());
            });
        Assert.NotNull(this._persistedResult);
        var published = Assert.Single(this._persistedResult!.Comments!);
        Assert.Null(published.OriginPassLens);
        Assert.Equal("Concrete defect 1.", published.Message);
    }

    [Fact]
    public async Task InsufficientContext_PublishesTheFindingOfAnOrdinaryResamplePass()
    {
        var judge = JudgeReturning("{\"verdict\":\"insufficient_context\",\"reason\":\"the caller is in another file\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("resample-model", judge),
            this.CreateProductionVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, UnionContext(null, ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        Assert.NotNull(this._persistedResult);
        Assert.Equal(2, this._persistedResult!.Comments!.Count);
    }

    [Fact]
    public async Task OrdinaryResamplePass_CodeContractFinding_IsJudgedLikeTheBaseline()
    {
        // A resample pass reviews with repository tools. Its CodeContract finding still reaches the judge, as the
        // baseline finding does, and both are published once the judge confirms them.
        var judge = JudgeReturning("{\"verdict\":\"confirmed\",\"reason\":\"present at line 3\"}");
        var reviewer = this.CreateReviewer(
            ResolverWithLowEffortJudge("resample-model", judge),
            this.CreateProductionVerificationExecutor());
        var file = FileForTier(FileComplexityTier.High);
        var (job, pr) = Fixture(file);

        await reviewer.ReviewAsync(
            job, pr, file, 1, 1, UnionContext(null, ToolsWithAnchorSource()), null, Substitute.For<IChatClient>(),
            CancellationToken.None);

        await judge.Received(2).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.NotNull(this._persistedResult);
        Assert.Equal(2, this._persistedResult!.Comments!.Count);
        // The second review call is the resample pass; its finding is published as union pass 2.
        Assert.Contains(this._persistedResult.Comments!, c => c.Message == "Concrete defect 2." && c.OriginPassIndex == 2);
    }
}
