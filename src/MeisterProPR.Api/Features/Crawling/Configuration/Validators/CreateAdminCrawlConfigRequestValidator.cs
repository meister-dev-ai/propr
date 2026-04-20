// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateAdminCrawlConfigRequest" /> before a new admin crawl configuration is created.</summary>
public sealed class CreateAdminCrawlConfigRequestValidator : AbstractValidator<CreateAdminCrawlConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateAdminCrawlConfigRequestValidator" />.</summary>
    public CreateAdminCrawlConfigRequestValidator()
    {
        this.RuleFor(r => r.ClientId)
            .NotEmpty()
            .WithMessage("ClientId is required.");

        this.RuleFor(r => r.ProviderScopePath)
            .Must(static url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("ProviderScopePath must be a valid absolute URL.")
            .When(r => !string.IsNullOrWhiteSpace(r.ProviderScopePath));

        this.RuleFor(r => r)
            .Must(r =>
                r.Provider == ScmProvider.AzureDevOps
                    ? r.OrganizationScopeId.HasValue || !string.IsNullOrWhiteSpace(r.ProviderScopePath)
                    : !string.IsNullOrWhiteSpace(r.ProviderScopePath))
            .WithMessage("Azure DevOps requires OrganizationScopeId or ProviderScopePath. Other providers require ProviderScopePath.");

        this.RuleFor(r => r.ProviderProjectKey)
            .NotEmpty()
            .WithMessage("ProviderProjectKey is required.");

        this.RuleFor(r => r.CrawlIntervalSeconds)
            .GreaterThanOrEqualTo(10)
            .WithMessage("CrawlIntervalSeconds must be >= 10.");

        this.RuleForEach(r => r.RepoFilters)
            .ChildRules(filter =>
            {
                filter.RuleFor(f => f.TargetBranchPatterns)
                    .NotNull()
                    .WithMessage("TargetBranchPatterns is required.");

                filter.RuleFor(f => f)
                    .Must(f =>
                        !string.IsNullOrWhiteSpace(f.RepositoryName) ||
                        !string.IsNullOrWhiteSpace(f.DisplayName) ||
                        (!string.IsNullOrWhiteSpace(f.CanonicalSourceRef?.Provider) &&
                         !string.IsNullOrWhiteSpace(f.CanonicalSourceRef?.Value)))
                    .WithMessage("Each repo filter requires a repository name, display name, or canonical source reference.");
            });

        this.RuleFor(r => r.ProCursorSourceIds)
            .Must(ids => ids is not null && ids.Any())
            .WithMessage("At least one ProCursor source is required when ProCursorSourceScopeMode is SelectedSources.")
            .When(r => r.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources);
    }
}
