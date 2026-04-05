// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateClientAdoOrganizationScopeRequest" /> before an organization scope is created.</summary>
public sealed class CreateClientAdoOrganizationScopeRequestValidator : AbstractValidator<CreateClientAdoOrganizationScopeRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateClientAdoOrganizationScopeRequestValidator" />.</summary>
    public CreateClientAdoOrganizationScopeRequestValidator()
    {
        this.RuleFor(r => r.OrganizationUrl)
            .NotEmpty()
            .WithMessage("OrganizationUrl is required.")
            .Must(BeValidAzureDevOpsOrganizationUrl)
            .WithMessage("OrganizationUrl must be a valid HTTPS Azure DevOps organization root.");

        this.RuleFor(r => r.DisplayName)
            .MaximumLength(256)
            .WithMessage("DisplayName must not exceed 256 characters.")
            .When(r => r.DisplayName is not null);
    }

    private static bool BeValidAzureDevOpsOrganizationUrl(string organizationUrl)
    {
        if (!Uri.TryCreate(organizationUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var organizationPath = uri.AbsolutePath.Trim('/');
            return !string.IsNullOrWhiteSpace(organizationPath) && !organizationPath.Contains('/');
        }

        return uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/'));
    }
}
