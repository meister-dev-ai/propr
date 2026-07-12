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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for the <c>pr_wide</c> review-pass scope in the per-file planner: a pr_wide-scope entry runs at the job
///     level, not per file, so <see cref="FileReviewer" />'s per-file planner skips it while a co-configured per-file
///     entry still runs. A pass list of only pr_wide entries leaves the per-file review byte-identical to a single
///     baseline pass. (PR-wide execution itself is a separate surface.)
/// </summary>
public sealed class FileReviewerPrWideScopeTests
{
    private readonly IAiReviewCore _aiCore = Substitute.For<IAiReviewCore>();
    private readonly IJobRepository _jobRepository = Substitute.For<IJobRepository>();
    private readonly IProtocolRecorder _recorder = Substitute.For<IProtocolRecorder>();

    private int _aiCallCount;

    public FileReviewerPrWideScopeTests()
    {
        this._recorder
            .BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(_ => Guid.NewGuid());
    }

    private FileReviewer CreateReviewer(IAiRuntimeResolver? aiRuntimeResolver)
    {
        this._aiCore
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ctx = ci.ArgAt<ReviewSystemContext>(1);
                ctx.LoopMetrics = new ReviewLoopMetrics(0, null, null, 90, 100, 10, 1);
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
            null);
    }

    private static IAiRuntimeResolver ResolverForAnyModel()
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(Substitute.For<IChatClient>());
        runtime.Model.Returns(new AiConfiguredModelDto(Guid.NewGuid(), "resample-model", "resample-model", [AiOperationKind.Chat], [AiProtocolMode.Auto]));
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeForModelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(runtime);
        return resolver;
    }

    private static ChangedFile HighTierFile()
    {
        var diff = new StringBuilder("@@ -1,1 +1,1 @@\n");
        for (var i = 0; i < 200; i++)
        {
            diff.Append("+ added line ").Append(i).Append('\n');
        }

        return new ChangedFile("Program.cs", ChangeType.Edit, "full content", diff.ToString(), false);
    }

    private static (ReviewJob job, PullRequest pr) Fixture(ChangedFile file)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 32, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 32, 1, "PR", null, "feature/x", "main",
            new List<ChangedFile> { file }.AsReadOnly());
        return (job, pr);
    }

    private static ReviewSystemContext ContextWith(params ReviewPassSpec[] passes)
    {
        return new ReviewSystemContext(null, [], null)
        {
            DefaultReviewChatClient = Substitute.For<IChatClient>(),
            EnableMultiPassUnion = true,
            ModelId = "gpt-5.3-codex",
            ReviewPasses = passes,
        };
    }

    [Fact]
    public async Task PrWideScopeOnly_IsSkippedByPerFilePlanner_SingleBaselinePass()
    {
        // A High-tier file would fan out for an ordinary per-file pass, but a pr_wide-scope entry runs at the job
        // level — the per-file planner skips it, leaving only the single baseline review.
        var reviewer = this.CreateReviewer(ResolverForAnyModel());
        var file = HighTierFile();
        var (job, pr) = Fixture(file);
        var context = ContextWith(new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide));

        await reviewer.ReviewAsync(job, pr, file, 1, 1, context, null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerFileEntry_AlongsidePrWide_OnlyPerFileEntryRunsPerFile()
    {
        // A per-file resample entry plus a pr_wide entry: only the per-file entry contributes a per-file pass, so the
        // baseline plus exactly one additional pass run — the pr_wide entry is skipped by the per-file planner.
        var reviewer = this.CreateReviewer(ResolverForAnyModel());
        var file = HighTierFile();
        var (job, pr) = Fixture(file);
        var context = ContextWith(
            new ReviewPassSpec(Guid.NewGuid()),
            new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide));

        await reviewer.ReviewAsync(job, pr, file, 1, 1, context, null, Substitute.For<IChatClient>(), CancellationToken.None);

        await this._aiCore.Received(2).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }
}
