// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using FluentValidation;
using MeisterProPR.Api.Controllers;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="PatchClientRequest" /> before a client is patched.</summary>
public sealed class PatchClientRequestValidator : AbstractValidator<PatchClientRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchClientRequestValidator" />.</summary>
    public PatchClientRequestValidator()
    {
        this.RuleFor(r => r.CustomSystemMessage)
            .MaximumLength(20_000)
            .WithMessage("CustomSystemMessage must not exceed 20,000 characters.")
            .When(r => r.CustomSystemMessage is not null);

        this.RuleFor(r => r.ScmCommentPostingEnabled)
            .Must(_ => true)
            .When(r => r.ScmCommentPostingEnabled.HasValue);

        this.RuleFor(r => r.EnableEvidenceBackedVerification)
            .Must(_ => true)
            .When(r => r.EnableEvidenceBackedVerification.HasValue);

        this.RuleFor(r => r.EnableLanguageRobustScreening)
            .Must(_ => true)
            .When(r => r.EnableLanguageRobustScreening.HasValue);

        this.RuleFor(r => r.EnableMultiPassUnion)
            .Must(_ => true)
            .When(r => r.EnableMultiPassUnion.HasValue);

        this.RuleFor(r => r.IncludeLinkedItemsInContext)
            .Must(_ => true)
            .When(r => r.IncludeLinkedItemsInContext.HasValue);

        this.RuleFor(r => r.BaselineReasoningEffort)
            .Must(effort => Enum.IsDefined(effort!.Value))
            .WithMessage("BaselineReasoningEffort must be one of none, low, medium, high.")
            .When(r => r.BaselineReasoningEffort.HasValue);

        this.RuleFor(r => r.ReviewPasses)
            .Must(BeAValidReviewPassList)
            .WithMessage(
                "ReviewPasses must contain at most 4 entries, each selecting exactly one of a non-empty "
                + "configuredModelId or a non-empty logicalModelName, an optional recognized lens, an optional "
                + "recognized scope, a recognized reasoning effort, a distinct (model, logicalModelName, lens, scope, "
                + "shadow) tuple, and unique, contiguous ordinals starting at 0.")
            .When(r => r.ReviewPasses is not null);

        this.RuleFor(r => r.BudgetConfig)
            .Must(BeAValidBudgetConfig)
            .WithMessage(
                "Budget caps must be non-negative, and a soft cap must not exceed its hard cap (for the monthly, "
                + "per-PR, and per-increment scopes).")
            .When(r => r.BudgetConfig is not null);
    }

    // Each cap must be non-negative (null means "no limit"), and where both a soft and a hard cap are set for the
    // same scope the soft cap must not exceed the hard one.
    private static bool BeAValidBudgetConfig(BudgetConfigDto? config)
    {
        if (config is null)
        {
            return true;
        }

        decimal?[] caps =
        [
            config.MonthlySoftCapUsd,
            config.MonthlyHardCapUsd,
            config.PullRequestSoftCapUsd,
            config.PullRequestHardCapUsd,
            config.IncrementSoftCapUsd,
            config.IncrementHardCapUsd,
        ];

        if (caps.Any(cap => cap is < 0m))
        {
            return false;
        }

        if (config is { MonthlySoftCapUsd: { } monthlySoft, MonthlyHardCapUsd: { } monthlyHard } && monthlySoft > monthlyHard)
        {
            return false;
        }

        if (config is { PullRequestSoftCapUsd: { } prSoft, PullRequestHardCapUsd: { } prHard } && prSoft > prHard)
        {
            return false;
        }

        if (config is { IncrementSoftCapUsd: { } incrementSoft, IncrementHardCapUsd: { } incrementHard } && incrementSoft > incrementHard)
        {
            return false;
        }

        return true;
    }

    // At most 4 additional passes (5 total with the implicit tier baseline), each bound to a non-empty configured
    // model, carrying at most a recognized lens and a recognized scope, forming a distinct (model, lens, scope, shadow)
    // tuple across entries, with ordinals that form a unique, contiguous 0..n-1 sequence. The model's existence and
    // chat-capability are checked separately (they need the client id and the database).
    private static bool BeAValidReviewPassList(IReadOnlyList<ReviewPassEntry>? passes)
    {
        if (passes is null)
        {
            return true;
        }

        if (passes.Count > 4)
        {
            return false;
        }

        // Each pass selects exactly one model source: a non-empty configuredModelId (legacy) OR a non-empty
        // logicalModelName (the named role) — never both, never neither.
        if (passes.Any(pass =>
            {
                var hasModelId = pass.ConfiguredModelId != Guid.Empty;
                var hasLogicalModel = !string.IsNullOrWhiteSpace(pass.LogicalModelName);
                return hasModelId == hasLogicalModel;
            }))
        {
            return false;
        }

        // Only null (an ordinary resample pass) or a recognized lens value is permitted.
        if (passes.Any(pass => !ReviewPassLens.IsValid(pass.Lens)))
        {
            return false;
        }

        // Only null (the per-file default) or a recognized scope value is permitted.
        if (passes.Any(pass => !ReviewPassScope.IsValid(pass.Scope)))
        {
            return false;
        }

        // Only a defined reasoning-effort level is permitted (none/low/medium/high).
        if (passes.Any(pass => !Enum.IsDefined(pass.ReasoningEffort)))
        {
            return false;
        }

        // Each pass must be a distinct (model, lens, scope, shadow) tuple — the same model under the same lens, scope,
        // and shadow flag twice is redundant resampling, which the ordered pass list exists to avoid. The same model
        // under a different lens/scope/shadow (e.g. a plain resample pass plus a security-lens pass on that model) is
        // allowed.
        if (passes.Select(pass => (pass.ConfiguredModelId, pass.LogicalModelName, pass.Lens, pass.Scope, pass.Shadow)).Distinct().Count() != passes.Count)
        {
            return false;
        }

        var ordinals = passes.Select(pass => pass.Ordinal).OrderBy(ordinal => ordinal).ToList();
        for (var index = 0; index < ordinals.Count; index++)
        {
            if (ordinals[index] != index)
            {
                return false;
            }
        }

        return true;
    }
}
