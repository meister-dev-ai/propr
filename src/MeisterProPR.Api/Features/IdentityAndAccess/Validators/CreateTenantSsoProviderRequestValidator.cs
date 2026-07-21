// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

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

        // Entra multi-tenant authorities accept genuinely-signed tokens from any Microsoft tenant, so their
        // email claim cannot be trusted as an account key. Each tenant must be configured against its own
        // specific authority (e.g. https://login.microsoftonline.com/<tenant-id>/v2.0).
        this.RuleFor(request => request.IssuerOrAuthorityUrl)
            .Must(NotBeMultiTenantEntraAuthority)
            .When(request => string.Equals(request.ProviderKind, "EntraId", StringComparison.Ordinal))
            .WithMessage(
                "Multi-tenant Entra authorities (common/organizations/consumers) are not supported. "
                + "Configure the authority for a specific tenant, e.g. https://login.microsoftonline.com/<tenant-id>/v2.0.");

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

    private static bool NotBeMultiTenantEntraAuthority(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            // URL shape is enforced by the HTTPS rule above; don't double-report here.
            return true;
        }

        var firstSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSegment is null
               || !(firstSegment.Equals("common", StringComparison.OrdinalIgnoreCase)
                    || firstSegment.Equals("organizations", StringComparison.OrdinalIgnoreCase)
                    || firstSegment.Equals("consumers", StringComparison.OrdinalIgnoreCase));
    }
}
