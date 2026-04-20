// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Features.Crawling.Webhooks.Validators;

/// <summary>Validates <see cref="CreateAdminWebhookConfigRequest" /> before a new webhook configuration is created.</summary>
public sealed class CreateAdminWebhookConfigRequestValidator : AbstractValidator<CreateAdminWebhookConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateAdminWebhookConfigRequestValidator" />.</summary>
    public CreateAdminWebhookConfigRequestValidator()
    {
        this.RuleFor(request => request.ClientId)
            .NotEmpty()
            .WithMessage("ClientId is required.");

        this.RuleFor(request => request.ProviderScopePath)
            .Must(static url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("ProviderScopePath must be a valid absolute URL.")
            .When(request => !string.IsNullOrWhiteSpace(request.ProviderScopePath));

        this.RuleFor(request => request)
            .Must(request =>
                request.Provider == WebhookProviderType.AzureDevOps
                    ? request.OrganizationScopeId.HasValue || !string.IsNullOrWhiteSpace(request.ProviderScopePath)
                    : !string.IsNullOrWhiteSpace(request.ProviderScopePath))
            .WithMessage("Azure DevOps requires OrganizationScopeId or ProviderScopePath. Other providers require ProviderScopePath.");

        this.RuleFor(request => request.ProviderProjectKey)
            .NotEmpty()
            .WithMessage("ProviderProjectKey is required.");

        this.RuleFor(request => request.EnabledEvents)
            .NotNull()
            .Must(events => events is not null && events.Count > 0)
            .WithMessage("At least one enabled event is required.");

        this.RuleForEach(request => request.RepoFilters)
            .ChildRules(filter =>
            {
                filter.RuleFor(item => item.TargetBranchPatterns)
                    .NotNull()
                    .WithMessage("TargetBranchPatterns is required.");

                filter.RuleFor(item => item)
                    .Must(item =>
                        !string.IsNullOrWhiteSpace(item.RepositoryName) ||
                        !string.IsNullOrWhiteSpace(item.DisplayName) ||
                        (!string.IsNullOrWhiteSpace(item.CanonicalSourceRef?.Provider) &&
                         !string.IsNullOrWhiteSpace(item.CanonicalSourceRef?.Value)))
                    .WithMessage("Each repo filter requires a repository name, display name, or canonical source reference.");
            });
    }
}
