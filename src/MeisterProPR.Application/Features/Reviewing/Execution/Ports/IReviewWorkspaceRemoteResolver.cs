// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Resolves provider-specific remote repository information needed for local git fetch operations.
/// </summary>
public interface IReviewWorkspaceRemoteResolver
{
    /// <summary>
    ///     Gets the provider handled by this resolver.
    /// </summary>
    ScmProvider Provider { get; }

    /// <summary>
    ///     Resolves remote access details for one review workspace request.
    /// </summary>
    Task<ReviewWorkspaceRemoteRef> ResolveAsync(ReviewRepositoryWorkspaceRequest request, CancellationToken ct);
}
