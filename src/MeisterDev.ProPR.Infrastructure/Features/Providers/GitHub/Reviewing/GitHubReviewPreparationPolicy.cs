// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>Prepares GitHub captured commit revision coordinates.</summary>
internal sealed class GitHubReviewPreparationPolicy : CodeReviewPreparationPolicyBase
{
    public override ScmProvider Provider => ScmProvider.GitHub;
}
