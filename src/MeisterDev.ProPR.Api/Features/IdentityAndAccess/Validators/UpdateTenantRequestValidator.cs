// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using FluentValidation;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess.Validators;

/// <summary>Validates tenant patch requests.</summary>
public sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    /// <summary>Creates the tenant patch validator.</summary>
    public UpdateTenantRequestValidator()
    {
        this.RuleFor(request => request)
            .Must(request =>
                request.DisplayName is not null
                || request.IsActive.HasValue
                || request.LocalLoginEnabled.HasValue
                || request.AllowedAiProviderKinds is not null
                || request.AllowedAiEndpointHosts is not null
                || request.RemovedUnresolvedAiProviderKinds is { Count: > 0 }
                || request.ReasoningCapturePolicy.HasValue
                || request.Budget is not null
                || request.ReviewLimits is not null)
            .WithMessage("At least one field must be provided.");

        this.RuleFor(request => request.ReasoningCapturePolicy)
            .IsInEnum()
            .WithMessage("ReasoningCapturePolicy must be one of installationDefault, enabled or disabled.")
            .When(request => request.ReasoningCapturePolicy.HasValue);

        this.RuleFor(request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName must not be empty.")
            .MaximumLength(200)
            .WithMessage("DisplayName must not exceed 200 characters.")
            .When(request => request.DisplayName is not null);

        this.RuleFor(request => request.Budget)
            .Must(BeAValidBudget)
            .WithMessage(
                "Budget caps must be non-negative, at most 999999999999.999999 with at most six fractional digits, "
                + "and the soft cap must not exceed the hard cap.")
            .When(request => request.Budget is not null);

        this.RuleFor(request => request.ReviewLimits)
            .Must(BeValidReviewLimits)
            .WithMessage(
                "The per-file byte limit must be at least 1024, and the structural parse limit must be between "
                + "1024 and 5242880.")
            .When(request => request.ReviewLimits is not null);
    }

    // The same ranges the installation-wide values accept, so a tenant cannot set a value the review pipeline
    // would refuse. A null value leaves the installation value in force.
    private static bool BeValidReviewLimits(TenantReviewLimitsDto? limits)
    {
        if (limits is null)
        {
            return true;
        }

        return limits.MaxFileSizeBytes is null or >= 1024
               && limits.MaxStructuralParseBytes is null or (>= 1024 and <= 5_242_880);
    }

    // Each cap must be non-negative (null means "no limit") and must fit the persisted numeric(18,6) columns, and
    // where both are set the soft cap must not exceed the hard one.
    private static bool BeAValidBudget(TenantBudgetConfigDto? budget)
    {
        if (budget is null)
        {
            return true;
        }

        if (budget.MonthlySoftCapUsd is < 0m || budget.MonthlyHardCapUsd is < 0m)
        {
            return false;
        }

        if (!MonetaryCapRange.IsStorable(budget.MonthlySoftCapUsd)
            || !MonetaryCapRange.IsStorable(budget.MonthlyHardCapUsd))
        {
            return false;
        }

        return budget is not { MonthlySoftCapUsd: { } soft, MonthlyHardCapUsd: { } hard } || soft <= hard;
    }
}
