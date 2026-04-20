// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Features.Clients.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="PatchClientProviderScopeRequest" /> before a provider scope is updated.</summary>
public sealed class PatchClientProviderScopeRequestValidator : AbstractValidator<PatchClientProviderScopeRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchClientProviderScopeRequestValidator" />.</summary>
    public PatchClientProviderScopeRequestValidator()
    {
        this.RuleFor(request => request)
            .Must(request => request.DisplayName is not null || request.IsEnabled.HasValue)
            .WithMessage("At least one field must be provided.");

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName must not be empty.")
            .MaximumLength(256)
            .WithMessage("DisplayName must not exceed 256 characters.")
            .When(request => request.DisplayName is not null);
    }
}
