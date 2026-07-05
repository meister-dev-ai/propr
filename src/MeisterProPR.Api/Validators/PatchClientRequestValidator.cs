// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="PatchClientRequest" /> before a client is patched.</summary>
public sealed class PatchClientRequestValidator : AbstractValidator<PatchClientRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchClientRequestValidator" />.</summary>
    public PatchClientRequestValidator()
    {
        this.RuleFor(r => r.CustomSystemMessage)
            .MaximumLength(20_000)
            .WithMessage("CustomSystemMessage must not exceed 20,000 characters.")
            .When(r => r.CustomSystemMessage is not null);

        this.RuleFor(r => r.ScmCommentPostingEnabled)
            .Must(_ => true)
            .When(r => r.ScmCommentPostingEnabled.HasValue);

        this.RuleFor(r => r.EnableProRV)
            .Must(_ => true)
            .When(r => r.EnableProRV.HasValue);

        this.RuleFor(r => r.EnableEvidenceBackedVerification)
            .Must(_ => true)
            .When(r => r.EnableEvidenceBackedVerification.HasValue);

        this.RuleFor(r => r.EnableMultiPassUnion)
            .Must(_ => true)
            .When(r => r.EnableMultiPassUnion.HasValue);

        this.RuleFor(r => r.ReviewPasses)
            .Must(BeAValidReviewPassList)
            .WithMessage(
                "ReviewPasses must contain at most 4 entries, each with a non-empty and distinct configuredModelId "
                + "and unique, contiguous ordinals starting at 0.")
            .When(r => r.ReviewPasses is not null);

        this.RuleFor(r => r.DefaultReviewStrategy)
            .Must(strategy => !strategy.HasValue || ReviewStrategyPolicy.IsSelectable(strategy.Value))
            .WithMessage(request => ReviewStrategyPolicy.GetDisabledSelectionMessage(request.DefaultReviewStrategy ?? ReviewStrategy.FileByFile));
    }

    // At most 4 additional passes (5 total with the implicit tier baseline), each bound to a non-empty configured
    // model that is distinct across entries, with ordinals that form a unique, contiguous 0..n-1 sequence. The
    // model's existence and chat-capability are checked separately (they need the client id and the database).
    private static bool BeAValidReviewPassList(IReadOnlyList<ReviewPassEntry>? passes)
    {
        if (passes is null)
        {
            return true;
        }

        if (passes.Count > 4)
        {
            return false;
        }

        if (passes.Any(pass => pass.ConfiguredModelId == Guid.Empty))
        {
            return false;
        }

        // Each pass must bind a distinct model — the same model twice is same-model resampling, which the
        // ordered pass list exists to avoid.
        if (passes.Select(pass => pass.ConfiguredModelId).Distinct().Count() != passes.Count)
        {
            return false;
        }

        var ordinals = passes.Select(pass => pass.Ordinal).OrderBy(ordinal => ordinal).ToList();
        for (var index = 0; index < ordinals.Count; index++)
        {
            if (ordinals[index] != index)
            {
                return false;
            }
        }

        return true;
    }
}
