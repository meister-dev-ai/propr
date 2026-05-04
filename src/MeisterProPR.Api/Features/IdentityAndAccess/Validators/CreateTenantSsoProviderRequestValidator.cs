// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Features.IdentityAndAccess.Validators;

/// <summary>Validates tenant SSO provider creation requests.</summary>
public sealed class CreateTenantSsoProviderRequestValidator : AbstractValidator<CreateTenantSsoProviderRequest>
{
    private static readonly string[] SupportedProviderKinds = ["EntraId", "Google", "GitHub"];
    private static readonly string[] SupportedProtocolKinds = ["Oidc", "Oauth2"];

    /// <summary>Creates the tenant SSO provider validator.</summary>
    public CreateTenantSsoProviderRequestValidator()
    {
        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.")
            .MaximumLength(200)
            .WithMessage("DisplayName must not exceed 200 characters.");

        this.RuleFor(request => request.ProviderKind)
            .NotEmpty()
            .WithMessage("ProviderKind is required.")
            .Must(kind => SupportedProviderKinds.Contains(kind, StringComparer.Ordinal))
            .WithMessage("ProviderKind must be one of EntraId, Google, or GitHub.");

        this.RuleFor(request => request.ProtocolKind)
            .NotEmpty()
            .WithMessage("ProtocolKind is required.")
            .Must(kind => SupportedProtocolKinds.Contains(kind, StringComparer.Ordinal))
            .WithMessage("ProtocolKind must be one of Oidc or Oauth2.");

        this.RuleFor(request => request.IssuerOrAuthorityUrl)
            .NotEmpty()
            .WithMessage("IssuerOrAuthorityUrl is required.")
            .Must(BeAbsoluteHttpsUri)
            .WithMessage("IssuerOrAuthorityUrl must be a valid HTTPS URL.");

        this.RuleFor(request => request.ClientId)
            .NotEmpty()
            .WithMessage("ClientId is required.")
            .MaximumLength(256)
            .WithMessage("ClientId must not exceed 256 characters.");

        this.RuleFor(request => request.ClientSecret)
            .NotEmpty()
            .WithMessage("ClientSecret is required.")
            .MaximumLength(4096)
            .WithMessage("ClientSecret must not exceed 4096 characters.");
    }

    private static bool BeAbsoluteHttpsUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }
}
