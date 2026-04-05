// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateClientRequest" /> before a client is registered.</summary>
public sealed class CreateClientRequestValidator : AbstractValidator<CreateClientRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateClientRequestValidator" />.</summary>
    public CreateClientRequestValidator()
    {
        this.RuleFor(r => r.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.");
    }
}
