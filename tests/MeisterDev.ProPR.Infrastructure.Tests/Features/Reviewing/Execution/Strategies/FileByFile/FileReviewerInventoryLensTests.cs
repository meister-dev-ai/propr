// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for the <c>inventory</c> review-pass lens in <see cref="FileReviewer" />: a text file with a diff runs
///     the pass on any tier; the pass context carries no repository tools, so the model judges the diff text alone;
///     a binary file skips the pass. Filtering of the inventory happens in the downstream union and verification
///     stages, so the pass itself only has to contribute candidates.
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

    private FileReviewer CreateReviewer(IAiRuntimeResolver? aiRuntimeResolver)
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
            null);
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

    private static ReviewSystemContext InventoryLensContext(IReviewContextTools? tools = null)
    {
        return new ReviewSystemContext(null, [], tools)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            ModelId = "gpt-5.3-codex",
            ReviewPasses = [new ReviewPassSpec(Guid.NewGuid(), ReviewPassLens.Inventory)],
        };
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
}
