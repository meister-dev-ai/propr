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

        this.RuleFor(r => r.MultiPassUnionPassCount)
            .InclusiveBetween(1, 5)
            .WithMessage("MultiPassUnionPassCount must be between 1 and 5.")
            .When(r => r.MultiPassUnionPassCount.HasValue);

        this.RuleFor(r => r.DefaultReviewStrategy)
            .Must(strategy => !strategy.HasValue || ReviewStrategyPolicy.IsSelectable(strategy.Value))
            .WithMessage(request => ReviewStrategyPolicy.GetDisabledSelectionMessage(request.DefaultReviewStrategy ?? ReviewStrategy.FileByFile));
    }
}
