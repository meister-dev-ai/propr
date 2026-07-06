// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Tests for <see cref="FileByFileSemanticScreeningStage" />: the flag-gated, demote-never-delete screening
///     path. Uses a fake screener so classification is deterministic without an embedding model.
/// </summary>
public sealed class FileByFileSemanticScreeningStageTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    [Fact]
    public async Task FlagOff_IsNoOp()
    {
        // Even a vague comment survives untouched when the client has not enabled language-robust screening.
        var context = BuildContext(
            false,
            false,
            new ReviewComment("a.cs", 1, CommentSeverity.Suggestion, "vague"));
        var stage = new FileByFileSemanticScreeningStage(new FakeScreener(_ => Vague()));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Single(result.ReviewResult!.Comments);
    }

    [Fact]
    public async Task FirmComments_AreKept()
    {
        var context = BuildContext(true, false, new ReviewComment("a.cs", 1, CommentSeverity.Warning, "firm bug"));
        var stage = new FileByFileSemanticScreeningStage(new FakeScreener(_ => Firm()));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Single(result.ReviewResult!.Comments);
    }

    [Fact]
    public async Task HedgedErrorWarning_WithVerifier_IsKeptForDownstreamVerification()
    {
        var context = BuildContext(true, true, new ReviewComment("a.cs", 1, CommentSeverity.Error, "hedged"));
        var stage = new FileByFileSemanticScreeningStage(new FakeScreener(_ => Hedged()));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        // Kept so the evidence verifier can confirm-or-summarize it; never deleted here.
        Assert.Single(result.ReviewResult!.Comments);
    }

    [Fact]
    public async Task HedgedError_WithoutVerifier_IsFoldedToSummary()
    {
        var context = BuildContext(true, false, new ReviewComment("a.cs", 7, CommentSeverity.Error, "hedged maybe"));
        var stage = new FileByFileSemanticScreeningStage(new FakeScreener(_ => Hedged()));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        // No verifier downstream to demote it, so it is folded into the summary rather than posted or deleted.
        Assert.Empty(result.ReviewResult!.Comments);
        Assert.Contains("hedged maybe", result.ReviewResult.Summary, StringComparison.Ordinal);
        Assert.Contains("a.cs:7", result.ReviewResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VagueSuggestion_IsFoldedToSummary_NotDeleted()
    {
        var context = BuildContext(true, false, new ReviewComment("a.cs", 1, CommentSeverity.Suggestion, "please tidy this"));
        var stage = new FileByFileSemanticScreeningStage(new FakeScreener(_ => Vague()));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Empty(result.ReviewResult!.Comments);
        Assert.Contains("please tidy this", result.ReviewResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DegradedScreening_KeepsEveryComment()
    {
        // Screening unavailable (no embedding model / failure): keep everything, including a comment that would
        // otherwise be folded — never drop on a screening error.
        var context = BuildContext(
            true,
            false,
            new ReviewComment("a.cs", 1, CommentSeverity.Suggestion, "would fold if screened"),
            new ReviewComment("a.cs", 2, CommentSeverity.Error, "firm"));
        var stage = new FileByFileSemanticScreeningStage(new FakeScreener(_ => CommentScreeningResult.DegradedFirm));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, result.ReviewResult!.Comments.Count);
    }

    private static CommentScreeningResult Firm()
    {
        return new CommentScreeningResult(CommentScreeningClass.Firm, 0.9);
    }

    private static CommentScreeningResult Hedged()
    {
        return new CommentScreeningResult(CommentScreeningClass.Hedged, 0.8);
    }

    private static CommentScreeningResult Vague()
    {
        return new CommentScreeningResult(CommentScreeningClass.Vague, 0.8);
    }

    private static PerFileReviewContext BuildContext(
        bool languageRobustScreening,
        bool evidenceVerification,
        params ReviewComment[] comments)
    {
        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var file = new ChangedFile("a.cs", ChangeType.Edit, "content", "@@ -1 +1 @@\n+line");
        var reviewContext = new ReviewSystemContext(null, [], null)
        {
            EnableLanguageRobustScreening = languageRobustScreening,
            EnableEvidenceBackedVerification = evidenceVerification,
        };

        return new PerFileReviewContext(
            job,
            file,
            null,
            reviewContext,
            Guid.NewGuid(),
            null,
            new ReviewResult("Base summary.", comments));
    }

    private sealed class FakeScreener(Func<string, CommentScreeningResult> classify) : ISemanticCommentScreener
    {
        public Task<CommentScreeningResult> ClassifyAsync(string commentText, Guid clientId, CancellationToken ct = default)
        {
            return Task.FromResult(classify(commentText));
        }
    }
}
