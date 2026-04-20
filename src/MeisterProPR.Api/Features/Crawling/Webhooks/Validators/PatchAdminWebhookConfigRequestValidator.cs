// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Features.Crawling.Webhooks.Validators;

/// <summary>Validates <see cref="PatchAdminWebhookConfigRequest" /> before a webhook configuration is updated.</summary>
public sealed class PatchAdminWebhookConfigRequestValidator : AbstractValidator<PatchAdminWebhookConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchAdminWebhookConfigRequestValidator" />.</summary>
    public PatchAdminWebhookConfigRequestValidator()
    {
        this.RuleFor(request => request.EnabledEvents)
            .Must(events => events is null || events.Count > 0)
            .WithMessage("EnabledEvents must contain at least one event when provided.");

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
