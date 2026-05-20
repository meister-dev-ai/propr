// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptsTemplateBackedSharedTests
{
    [Fact]
    public void BuildGlobalSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(null);

        Assert.Contains("expert code reviewer specialising in general software engineering best practices", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CERTAINTY GATE", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The very first character must be '{'", prompt, StringComparison.Ordinal);
        Assert.Contains("confidence_evaluations", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrVerificationSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildPrVerificationSystemPrompt(null);

        Assert.Contains("independently retrieved repository evidence", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recommended_disposition", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrVerificationUserMessage_UsesTemplateBackedDefault()
    {
        var claim = new ClaimDescriptor(
            "claim-1",
            "finding-1",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Cross-file DI registration is missing.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CrossFileConsistencyFamily,
            "ServiceRegistration",
            requiresCrossFileEvidence: true,
            requiresSymbolEvidence: true);
        var evidence = new EvidenceBundle(
            claim.ClaimId,
            [new EvidenceItem("FileContentRange", "Fetched file", "src/Foo.cs", "services.AddFoo();")],
            EvidenceBundle.PartialCoverage,
            "One supporting file was retrieved.");

        var message = ReviewPrompts.BuildPrVerificationUserMessage(claim, evidence);

        Assert.Contains("Claim ID: claim-1", message, StringComparison.Ordinal);
        Assert.Contains("Payload: services.AddFoo();", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSynthesisSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(null, true);

        Assert.Contains("cross_cutting_concerns", prompt, StringComparison.Ordinal);
        Assert.Contains("The very first character must be '{'", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQualityFilterSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildQualityFilterSystemPrompt(null);

        Assert.Contains("senior code review editor", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISCARD", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
