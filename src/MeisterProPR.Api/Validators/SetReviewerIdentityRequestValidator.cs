// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="SetReviewerIdentityRequest" /> before a reviewer identity is stored.</summary>
public sealed class SetReviewerIdentityRequestValidator : AbstractValidator<SetReviewerIdentityRequest>
{
    /// <summary>Initializes a new instance of <see cref="SetReviewerIdentityRequestValidator" />.</summary>
    public SetReviewerIdentityRequestValidator()
    {
        this.RuleFor(r => r.ReviewerId)
            .NotEqual(Guid.Empty)
            .WithMessage("ReviewerId must not be an empty GUID.");
    }
}
