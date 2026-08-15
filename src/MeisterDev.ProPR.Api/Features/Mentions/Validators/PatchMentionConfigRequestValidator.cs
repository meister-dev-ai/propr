// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterDev.ProPR.Api.Controllers;

namespace MeisterDev.ProPR.Api.Validators;

/// <summary>Validates <see cref="PatchMentionConfigRequest" /> before a mention configuration is changed.</summary>
/// <remarks>
///     Every field is optional, so each rule applies only when the caller sent that field. A repository list
///     that is present must not be empty: sending one is how an operator says which repositories to answer
///     on, and an empty one would silently stop the configuration answering anything.
/// </remarks>
public sealed class PatchMentionConfigRequestValidator : AbstractValidator<PatchMentionConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchMentionConfigRequestValidator" />.</summary>
    public PatchMentionConfigRequestValidator()
    {
        this.RuleFor(r => r.ScanIntervalSeconds)
            .GreaterThanOrEqualTo(10)
            .WithMessage("ScanIntervalSeconds must be >= 10.")
            .LessThanOrEqualTo(86_400)
            .WithMessage("ScanIntervalSeconds must be at most 86400 (one day).")
            .When(r => r.ScanIntervalSeconds.HasValue);

        this.RuleFor(r => r.RepoFilters)
            .NotEmpty()
            .WithMessage("A mention configuration must name at least one repository.")
            .When(r => r.RepoFilters is not null);

        // NotNull before SetValidator, because SetValidator skips a null child instead of rejecting it and
        // the controller would then dereference it.
        this.RuleForEach(r => r.RepoFilters)
            .NotNull()
            .WithMessage("A repository entry must not be null.")
            .SetValidator(new MentionRepoFilterRequestValidator()!)
            .When(r => r.RepoFilters is not null);
    }
}
