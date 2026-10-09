// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>Provides the production local source policies to integration fixtures with substituted remote adapters.</summary>
public static class ReviewSourcePolicies
{
    public static IReviewSourcePolicy Get(ScmProvider provider) => LocalScmPolicies.Registry.GetReviewSourcePolicy(provider);
}
