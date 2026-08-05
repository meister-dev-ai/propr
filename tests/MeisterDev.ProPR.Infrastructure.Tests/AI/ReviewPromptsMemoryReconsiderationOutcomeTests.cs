// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     The reconsideration prompt states what a reviewer's resolution meant rather than leaving the model to
///     infer it from prose. A rejection and a claimed fix imply opposite actions on a recurrence, and an
///     acceptance the discussion never made explicit is worth less than one it did.
/// </summary>
public sealed class ReviewPromptsMemoryReconsiderationOutcomeTests
{
    [Fact]
    public void BuildMemoryReconsiderationUserMessage_RejectionMatchedOnContent_GivesTheConfidentInstruction()
    {
        var message = ReviewPrompts.BuildMemoryReconsiderationUserMessage(
            "{}",
            [Match(ThreadResolutionIntent.AcceptedByHuman, ResolutionClarity.AcceptedWithoutChange, "semantic")]);

        Assert.Contains(
            "A reviewer rejected this concern and accepted the code as it stands. **DISCARD**",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMemoryReconsiderationUserMessage_UnclearRejection_MarksItLowerConfidence()
    {
        var message = ReviewPrompts.BuildMemoryReconsiderationUserMessage(
            "{}",
            [Match(ThreadResolutionIntent.AcceptedByHuman, ResolutionClarity.ClosedWithoutResolution, "semantic")]);

        Assert.Contains("the discussion did not say so plainly", message, StringComparison.Ordinal);
        Assert.DoesNotContain("**DISCARD** a draft finding", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMemoryReconsiderationUserMessage_RejectionFoundOnlyByFilePath_DoesNotGetTheConfidentInstruction()
    {
        var message = ReviewPrompts.BuildMemoryReconsiderationUserMessage(
            "{}",
            [
                Match(
                    ThreadResolutionIntent.AcceptedByHuman,
                    ResolutionClarity.AcceptedWithoutChange,
                    "exact_file_fallback"),
            ]);

        Assert.Contains("found only because it sits on the same file", message, StringComparison.Ordinal);
        Assert.DoesNotContain("**DISCARD** a draft finding", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMemoryReconsiderationUserMessage_ClaimedFix_DoesNotJustifyDiscarding()
    {
        var message = ReviewPrompts.BuildMemoryReconsiderationUserMessage(
            "{}",
            [Match(ThreadResolutionIntent.ClaimsFix, ResolutionClarity.ResolvedByChange, "semantic")]);

        Assert.Contains("does not justify discarding a finding", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMemoryReconsiderationUserMessage_NoRecordedOutcome_ClaimsNothingAboutTheResolution()
    {
        var message = ReviewPrompts.BuildMemoryReconsiderationUserMessage(
            "{}",
            [Match(null, null, "semantic")]);

        Assert.DoesNotContain("Reviewer outcome", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMemoryReconsiderationUserMessage_AdminDismissedPattern_ClaimsNoReviewerOutcome()
    {
        var message = ReviewPrompts.BuildMemoryReconsiderationUserMessage(
            "{}",
            [
                Match(
                    ThreadResolutionIntent.AcceptedByHuman,
                    ResolutionClarity.AcceptedWithoutChange,
                    "semantic",
                    MemorySource.AdminDismissed),
            ]);

        Assert.DoesNotContain("Reviewer outcome", message, StringComparison.Ordinal);
        Assert.Contains("ADMIN-DISMISSED PATTERN", message, StringComparison.Ordinal);
    }

    private static ThreadMemoryMatchDto Match(
        ThreadResolutionIntent? intent,
        ResolutionClarity? clarity,
        string matchSource,
        MemorySource source = MemorySource.ThreadResolved)
    {
        return new ThreadMemoryMatchDto(
            Guid.NewGuid(),
            "42",
            "backend/Foo.cs",
            "The resolution summary.",
            0.92f,
            matchSource,
            source,
            intent,
            clarity);
    }
}
