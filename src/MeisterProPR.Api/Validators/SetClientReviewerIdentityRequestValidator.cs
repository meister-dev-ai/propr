// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Features.Clients.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="SetClientReviewerIdentityRequest" /> before a reviewer identity is stored.</summary>
public sealed class SetClientReviewerIdentityRequestValidator : AbstractValidator<SetClientReviewerIdentityRequest>
{
    /// <summary>Initializes a new instance of <see cref="SetClientReviewerIdentityRequestValidator" />.</summary>
    public SetClientReviewerIdentityRequestValidator()
    {
        this.RuleFor(request => request.ExternalUserId)
            .NotEmpty()
            .WithMessage("ExternalUserId is required.")
            .MaximumLength(256)
            .WithMessage("ExternalUserId must not exceed 256 characters.");

        this.RuleFor(request => request.Login)
            .NotEmpty()
            .WithMessage("Login is required.")
            .MaximumLength(256)
            .WithMessage("Login must not exceed 256 characters.");

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.")
            .MaximumLength(256)
            .WithMessage("DisplayName must not exceed 256 characters.");
    }
}
