// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>Prepares Azure DevOps captured revision and iteration comparison coordinates.</summary>
internal sealed class AdoReviewPreparationPolicy : CodeReviewPreparationPolicyBase
{
    public override ScmProvider Provider => ScmProvider.AzureDevOps;

    public override ThreadResolutionIntent InterpretThreadResolution(string? status)
    {
        if (string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase))
        {
            return ThreadResolutionIntent.AcceptedByHuman;
        }

        if (string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase))
        {
            return ThreadResolutionIntent.ClaimsFix;
        }

        return ThreadResolutionIntent.Active;
    }

    public override bool RequiresLiveRevisionRefresh(ReviewRevision? revision) => false;
    public override object CreatePublicationContext(int? compareToIterationId) => new AzureDevOpsPublicationContext(compareToIterationId);

    public override ReviewRevision ResolveStoredRevision(ReviewJob job)
    {
        if (job.ReviewRevisionReference is { } revision)
        {
            return revision;
        }

        var iteration = job.IterationId.ToString(CultureInfo.InvariantCulture);
        return new($"ado-head-{iteration}", $"ado-base-{iteration}", null, iteration, null);
    }

    public override ReviewComparisonHandle SelectComparisonHandle(ReviewJob job, ReviewJob baseline)
    {
        var iteration = ReviewRevisionKeys.TryParseIterationId(ReviewRevisionKeys.TryGetStoredKey(baseline.ReviewRevisionReference))
                        ?? (baseline.IterationId > 0 ? baseline.IterationId : (int?)null);
        return iteration is > 0 && iteration < job.IterationId
            ? new(true, iteration)
            : new(false);
    }
}
