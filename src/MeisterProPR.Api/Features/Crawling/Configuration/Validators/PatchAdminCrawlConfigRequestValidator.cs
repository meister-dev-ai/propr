// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="PatchAdminCrawlConfigRequest" /> before a crawl configuration is updated by an admin.</summary>
public sealed class PatchAdminCrawlConfigRequestValidator : AbstractValidator<PatchAdminCrawlConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchAdminCrawlConfigRequestValidator" />.</summary>
    public PatchAdminCrawlConfigRequestValidator()
    {
        this.RuleFor(r => r.CrawlIntervalSeconds)
            .GreaterThanOrEqualTo(10)
            .WithMessage("CrawlIntervalSeconds must be >= 10.")
            .When(r => r.CrawlIntervalSeconds.HasValue);

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
                        (!string.IsNullOrWhiteSpace(f.CanonicalSourceRef?.Provider) && !string.IsNullOrWhiteSpace(f.CanonicalSourceRef?.Value)))
                    .WithMessage("Each repo filter requires a repository name, display name, or canonical source reference.");
            });

        this.RuleFor(r => r.ProCursorSourceIds)
            .Must(ids => ids is not null && ids.Any())
            .WithMessage("At least one ProCursor source is required when ProCursorSourceScopeMode is SelectedSources.")
            .When(r => r.ProCursorSourceScopeMode == MeisterProPR.Domain.Enums.ProCursorSourceScopeMode.SelectedSources);
    }
}
