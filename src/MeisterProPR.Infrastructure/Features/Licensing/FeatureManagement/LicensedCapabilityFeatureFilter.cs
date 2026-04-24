// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.FeatureManagement;

namespace MeisterProPR.Infrastructure.Features.Licensing.FeatureManagement;

/// <summary>Terminal feature filter used by persisted feature definitions that are already pre-resolved by licensing state.</summary>
[FilterAlias(FilterAliasName)]
public sealed class LicensedCapabilityFeatureFilter : IFeatureFilter
{
    public const string FilterAliasName = "licensed-capability";

    public Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context)
    {
        return Task.FromResult(true);
    }
}
