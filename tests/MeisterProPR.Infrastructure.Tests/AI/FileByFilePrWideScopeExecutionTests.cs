// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests the job-level PR-wide-scope execution hook in <see cref="FileByFileReviewOrchestrator" />: a pr_wide-scope
///     pass entry runs the generate-only PR-wide generator once at the job level (after the per-file fan-out) and its
///     candidates flow through the shared verify -> gate -> publish path with "Pass N" / PR-wide provenance. A review
///     with no pr_wide entry never touches the generator. An entry whose model cannot be resolved is skipped while the
///     rest run. Fakes only; no model calls.
/// </summary>
public sealed class FileByFilePrWideScopeExecutionTests
{
    private const string SynthesisJson =
        """
        {
          "summary": "Base summary.",
          "cross_cutting_concerns": []
        }
        """;

    [Fact]
    public async Task ReviewAsync_WithPrWideEntry_RunsGeneratorOnceAndPublishesWithPrWideProvenance()
    {
        var generator = Substitute.For<IPrWideCandidateGenerator>();
        generator.GenerateCandidatesAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<IResolvedAiChatRuntime>(),
                Arg.Any<PrWideGenerationBudget>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([PublishablePrWideFinding(unionPassIndex: 2)]);

        var fixture = BuildFixture(generator, ResolverForAnyModel());
        var context = ContextWith(fixture.ChatClient, fixture.ReviewTools, new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide));

        var result = await fixture.Sut.ReviewAsync(fixture.Job, fixture.Pr, context, CancellationToken.None);

        await generator.Received(1).GenerateCandidatesAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<IResolvedAiChatRuntime>(),
            Arg.Is<PrWideGenerationBudget>(budget =>
                budget.MaxInvestigations == 3 && budget.MaxToolCallsPerInvestigation == 3 && budget.MaxSeedFilesPerInvestigation == 5),
            2,
            false,
            Arg.Any<CancellationToken>());

        var prWideComment = Assert.Single(result.Comments, comment => comment.Message == "PR-wide cross-cutting concern.");
        Assert.Equal(nameof(ReviewPassKind.MultiPassUnion), prWideComment.OriginPassKind);
        Assert.Equal(2, prWideComment.OriginPassIndex);
        Assert.Equal(ReviewPassScope.PrWide, prWideComment.OriginPassLens);
    }

    [Fact]
    public async Task ReviewAsync_WithAnchoredEvidenceBackedPrWideFinding_PublishesInlineCommentAtAnchor()
    {
        var generator = Substitute.For<IPrWideCandidateGenerator>();
        generator.GenerateCandidatesAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<IResolvedAiChatRuntime>(),
                Arg.Any<PrWideGenerationBudget>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([VerifierApprovedAnchoredPrWideFinding(unionPassIndex: 2)]);

        var fixture = BuildFixture(generator, ResolverForAnyModel());
        var context = ContextWith(fixture.ChatClient, fixture.ReviewTools, new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide));

        var result = await fixture.Sut.ReviewAsync(fixture.Job, fixture.Pr, context, CancellationToken.None);

        var prWideComment = Assert.Single(result.Comments, comment => comment.Message == "Anchored cross-file concern with resolved evidence.");
        Assert.Equal("src/Foo.cs", prWideComment.FilePath);
        Assert.Equal(5, prWideComment.LineNumber);
        Assert.Equal(nameof(ReviewPassKind.MultiPassUnion), prWideComment.OriginPassKind);
        Assert.Equal(2, prWideComment.OriginPassIndex);
        Assert.Equal(ReviewPassScope.PrWide, prWideComment.OriginPassLens);
    }

    [Fact]
    public async Task ReviewAsync_WithUnanchoredPrWideFinding_DoesNotPublish()
    {
        var generator = Substitute.For<IPrWideCandidateGenerator>();
        generator.GenerateCandidatesAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<IResolvedAiChatRuntime>(),
                Arg.Any<PrWideGenerationBudget>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([UnanchoredPrWideFinding(unionPassIndex: 2)]);

        var fixture = BuildFixture(generator, ResolverForAnyModel());
        var context = ContextWith(fixture.ChatClient, fixture.ReviewTools, new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide));

        var result = await fixture.Sut.ReviewAsync(fixture.Job, fixture.Pr, context, CancellationToken.None);

        Assert.DoesNotContain(result.Comments, comment => comment.Message == "Unanchored cross-file concern.");
    }

    [Fact]
    public async Task ReviewAsync_WithShadowPrWideEntry_RunsGeneratorButDoesNotPublish()
    {
        var generator = Substitute.For<IPrWideCandidateGenerator>();
        generator.GenerateCandidatesAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<IResolvedAiChatRuntime>(),
                Arg.Any<PrWideGenerationBudget>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([PublishablePrWideFinding(unionPassIndex: 2)]);

        var fixture = BuildFixture(generator, ResolverForAnyModel());
        var context = ContextWith(
            fixture.ChatClient,
            fixture.ReviewTools,
            new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide, Shadow: true));

        var result = await fixture.Sut.ReviewAsync(fixture.Job, fixture.Pr, context, CancellationToken.None);

        // The shadow entry still runs the generator (which records its trace + catch count) with shadow = true...
        await generator.Received(1).GenerateCandidatesAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<IResolvedAiChatRuntime>(),
            Arg.Any<PrWideGenerationBudget>(),
            2,
            true,
            Arg.Any<CancellationToken>());
        // ...but its finding is never published.
        Assert.DoesNotContain(result.Comments, comment => comment.Message == "PR-wide cross-cutting concern.");
    }

    [Fact]
    public async Task ReviewAsync_WithoutPrWideEntry_NeverTouchesGenerator()
    {
        var generator = Substitute.For<IPrWideCandidateGenerator>();
        var fixture = BuildFixture(generator, ResolverForAnyModel());

        // A per-file resample entry only (no pr_wide scope): the job-level PR-wide hook must not fire.
        var context = ContextWith(fixture.ChatClient, fixture.ReviewTools, new ReviewPassSpec(Guid.NewGuid()));

        var result = await fixture.Sut.ReviewAsync(fixture.Job, fixture.Pr, context, CancellationToken.None);

        await generator.DidNotReceive().GenerateCandidatesAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<IResolvedAiChatRuntime>(),
            Arg.Any<PrWideGenerationBudget>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        Assert.Contains(result.Comments, comment => comment.Message == "Confirmed local issue.");
        Assert.DoesNotContain(result.Comments, comment => comment.Message == "PR-wide cross-cutting concern.");
    }

    [Fact]
    public async Task ReviewAsync_UnresolvablePrWideModel_SkipsThatEntryAndRunsTheRest()
    {
        var unresolvableModelId = Guid.NewGuid();
        var resolvableModelId = Guid.NewGuid();

        var resolvableRuntime = RuntimeForModel();
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), unresolvableModelId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("model deleted"));
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), resolvableModelId, Arg.Any<CancellationToken>())
            .Returns(resolvableRuntime);

        var generator = Substitute.For<IPrWideCandidateGenerator>();
        generator.GenerateCandidatesAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<IResolvedAiChatRuntime>(),
                Arg.Any<PrWideGenerationBudget>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var fixture = BuildFixture(generator, resolver);

        // First entry is unresolvable (skipped); the second entry (list ordinal 1) resolves and runs as pass #3.
        var context = ContextWith(
            fixture.ChatClient,
            fixture.ReviewTools,
            new ReviewPassSpec(unresolvableModelId, Scope: ReviewPassScope.PrWide),
            new ReviewPassSpec(resolvableModelId, Scope: ReviewPassScope.PrWide));

        var result = await fixture.Sut.ReviewAsync(fixture.Job, fixture.Pr, context, CancellationToken.None);

        await generator.Received(1).GenerateCandidatesAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<IResolvedAiChatRuntime>(),
            Arg.Any<PrWideGenerationBudget>(),
            3,
            false,
            Arg.Any<CancellationToken>());
        Assert.Contains(result.Comments, comment => comment.Message == "Confirmed local issue.");
    }

    private static CandidateReviewFinding PublishablePrWideFinding(int unionPassIndex)
    {
        return new CandidateReviewFinding(
            $"finding-prw-{unionPassIndex:D2}-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PrWidePassOrigin,
                "pr_wide_pass",
                reviewPassKind: ReviewPassKind.MultiPassUnion,
                unionPassIndex: unionPassIndex,
                unionLens: ReviewPassScope.PrWide),
            CommentSeverity.Warning,
            "PR-wide cross-cutting concern.",
            CandidateReviewFinding.CrossCuttingCategory,
            "src/Foo.cs",
            5,
            verificationOutcome: new VerificationOutcome(
                "claim-prw",
                $"finding-prw-{unionPassIndex:D2}-001",
                VerificationOutcome.SupportedKind,
                FinalGateDecision.PublishDisposition,
                [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                [],
                VerificationOutcome.StrongEvidence,
                "The retrieved repository evidence supports the cross-file claim.",
                VerificationOutcome.AiMicroVerifierEvaluator,
                false),
            scopeRelation: ChangedLineRelation.OnChangedLine);
    }

    // A verifier-approved anchored PR-wide finding: the bounded PR-verifier recommended publication and the anchor
    // sits on a changed line, so the PR-wide gate branch publishes it as an inline thread. The verifier's Publish
    // verdict — not the resolved multi-file evidence — is what earns publication.
    private static CandidateReviewFinding VerifierApprovedAnchoredPrWideFinding(int unionPassIndex)
    {
        return new CandidateReviewFinding(
            $"finding-prw-{unionPassIndex:D2}-002",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PrWidePassOrigin,
                "pr_wide_pass",
                reviewPassKind: ReviewPassKind.MultiPassUnion,
                unionPassIndex: unionPassIndex,
                unionLens: ReviewPassScope.PrWide),
            CommentSeverity.Warning,
            "Anchored cross-file concern with resolved evidence.",
            CandidateReviewFinding.CrossCuttingCategory,
            "src/Foo.cs",
            5,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            "Potential cross-file concern noted.",
            verificationOutcome: new VerificationOutcome(
                "claim-prw",
                $"finding-prw-{unionPassIndex:D2}-002",
                VerificationOutcome.SupportedKind,
                FinalGateDecision.PublishDisposition,
                [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                [],
                VerificationOutcome.StrongEvidence,
                "The retrieved repository evidence supports the cross-file claim.",
                VerificationOutcome.AiMicroVerifierEvaluator,
                false),
            scopeRelation: ChangedLineRelation.OnChangedLine);
    }

    // A verifier-approved PR-wide finding with no changed-line anchor (null file/line/scope). It is earned but not
    // located, so the PR-wide gate branch keeps it summary-only and it never publishes. This isolates the
    // "verifier-approved but unanchored -> not published" behavior.
    private static CandidateReviewFinding UnanchoredPrWideFinding(int unionPassIndex)
    {
        return new CandidateReviewFinding(
            $"finding-prw-{unionPassIndex:D2}-003",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PrWidePassOrigin,
                "pr_wide_pass",
                reviewPassKind: ReviewPassKind.MultiPassUnion,
                unionPassIndex: unionPassIndex,
                unionLens: ReviewPassScope.PrWide),
            CommentSeverity.Warning,
            "Unanchored cross-file concern.",
            CandidateReviewFinding.CrossCuttingCategory,
            null,
            null,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            "Potential unanchored cross-file concern noted.",
            verificationOutcome: new VerificationOutcome(
                "claim-prw",
                $"finding-prw-{unionPassIndex:D2}-003",
                VerificationOutcome.SupportedKind,
                FinalGateDecision.PublishDisposition,
                [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                [],
                VerificationOutcome.StrongEvidence,
                "The retrieved repository evidence supports the cross-file claim.",
                VerificationOutcome.AiMicroVerifierEvaluator,
                false));
    }

    private static IAiRuntimeResolver ResolverForAnyModel()
    {
        var runtime = RuntimeForModel();
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(runtime);
        return resolver;
    }

    private static IResolvedAiChatRuntime RuntimeForModel()
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(Substitute.For<IChatClient>());
        runtime.Model.Returns(new AiConfiguredModelDto(Guid.NewGuid(), "prwide-model", "prwide-model", [AiOperationKind.Chat], [AiProtocolMode.Auto]));
        return runtime;
    }

    private static ReviewSystemContext ContextWith(IChatClient chatClient, IReviewContextTools reviewTools, params ReviewPassSpec[] passes)
    {
        return new ReviewSystemContext(null, [], reviewTools)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = "test-model",
            ModelId = "test-model",
            Temperature = 0.25f,
            ReviewPasses = passes,
        };
    }

    private static Fixture BuildFixture(IPrWideCandidateGenerator generator, IAiRuntimeResolver resolver)
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed local issue.")]));

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns([new ChangedFileSummary("src/Foo.cs", ChangeType.Edit)]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("no_result", [], "No evidence."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 1, "Test PR", null, "feature/x", "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "@@ -1 +1 @@\n+changed")]);

        var storedResults = new List<ReviewFileResult>();
        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storedResults.Add(call.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, SynthesisJson)));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            aiRuntimeResolver: resolver,
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            prWideCandidateGeneratorFactory: () => generator);

        return new Fixture(sut, job, pr, chatClient, reviewTools);
    }

    private static ReviewJob BuildJobWithResults(ReviewJob original, IEnumerable<ReviewFileResult> results)
    {
        var job = new ReviewJob(
            original.Id,
            original.ClientId,
            original.OrganizationUrl,
            original.ProjectId,
            original.RepositoryId,
            original.PullRequestId,
            original.IterationId);

        foreach (var result in results)
        {
            job.FileReviewResults.Add(result);
        }

        return job;
    }

    private sealed record Fixture(
        FileByFileReviewOrchestrator Sut,
        ReviewJob Job,
        PullRequest Pr,
        IChatClient ChatClient,
        IReviewContextTools ReviewTools);
}
