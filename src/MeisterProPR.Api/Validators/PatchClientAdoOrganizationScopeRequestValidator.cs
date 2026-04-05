// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="PatchClientAdoOrganizationScopeRequest" /> before an organization scope is updated.</summary>
public sealed class PatchClientAdoOrganizationScopeRequestValidator : AbstractValidator<PatchClientAdoOrganizationScopeRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchClientAdoOrganizationScopeRequestValidator" />.</summary>
    public PatchClientAdoOrganizationScopeRequestValidator()
    {
        this.RuleFor(r => r)
            .Must(HaveAnyChanges)
            .WithMessage("At least one field must be provided.");

        this.RuleFor(r => r.OrganizationUrl)
            .Must(BeValidAzureDevOpsOrganizationUrl)
            .WithMessage("OrganizationUrl must be a valid HTTPS Azure DevOps organization root.")
            .When(r => r.OrganizationUrl is not null);

        this.RuleFor(r => r.DisplayName)
            .MaximumLength(256)
            .WithMessage("DisplayName must not exceed 256 characters.")
            .When(r => r.DisplayName is not null);
    }

    private static bool HaveAnyChanges(PatchClientAdoOrganizationScopeRequest request)
    {
        return request.OrganizationUrl is not null ||
               request.DisplayName is not null ||
               request.IsEnabled.HasValue;
    }

    private static bool BeValidAzureDevOpsOrganizationUrl(string? organizationUrl)
    {
        if (string.IsNullOrWhiteSpace(organizationUrl) ||
            !Uri.TryCreate(organizationUrl, UriKind.Absolute, out var uri) ||
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
