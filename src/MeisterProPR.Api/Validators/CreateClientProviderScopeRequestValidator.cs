// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Features.Clients.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateClientProviderScopeRequest" /> before a provider scope is created.</summary>
public sealed class CreateClientProviderScopeRequestValidator : AbstractValidator<CreateClientProviderScopeRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateClientProviderScopeRequestValidator" />.</summary>
    public CreateClientProviderScopeRequestValidator()
    {
        this.RuleFor(request => request.ScopeType)
            .NotEmpty()
            .WithMessage("ScopeType is required.")
            .MaximumLength(64)
            .WithMessage("ScopeType must not exceed 64 characters.");

        this.RuleFor(request => request.ExternalScopeId)
            .NotEmpty()
            .WithMessage("ExternalScopeId is required.")
            .MaximumLength(256)
            .WithMessage("ExternalScopeId must not exceed 256 characters.");

        this.RuleFor(request => request.ScopePath)
            .NotEmpty()
            .WithMessage("ScopePath is required.")
            .MaximumLength(512)
            .WithMessage("ScopePath must not exceed 512 characters.");

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.")
            .MaximumLength(256)
            .WithMessage("DisplayName must not exceed 256 characters.");
    }
}
