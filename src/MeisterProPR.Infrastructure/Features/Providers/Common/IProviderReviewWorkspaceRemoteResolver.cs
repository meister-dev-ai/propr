// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal interface IProviderReviewWorkspaceRemoteResolver
{
    ScmProvider Provider { get; }

    Task<ReviewWorkspaceRemoteRef> ResolveAsync(ReviewRepositoryWorkspaceRequest request, CancellationToken ct);
}
