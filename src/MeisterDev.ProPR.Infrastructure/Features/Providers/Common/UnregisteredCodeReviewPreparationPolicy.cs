// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Reads normalized stored revision data without supplying runtime provider capabilities.</summary>
internal sealed class UnregisteredCodeReviewPreparationPolicy(ScmProvider provider) : CodeReviewPreparationPolicyBase
{
    public override ScmProvider Provider => provider;
}
