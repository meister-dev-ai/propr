// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Provider-neutral publication context carried from orchestration into provider adapters.
/// </summary>
public sealed record ReviewPublicationContext(
    CodeReviewRef Review,
    ReviewRevision Revision,
    ReviewerIdentity AuthorizedPublicationIdentity,
    IReadOnlyList<PrCommentThread> ExistingThreads,
    object? ProviderSpecificContext = null)
{
    /// <summary>
    ///     Returns the provider-specific context when it matches the requested type.
    /// </summary>
    public TContext? GetProviderSpecificContext<TContext>()
        where TContext : class
    {
        return this.ProviderSpecificContext as TContext;
    }
}

/// <summary>
///     Azure DevOps publication details needed to recreate the diff that was reviewed.
/// </summary>
public sealed record AzureDevOpsPublicationContext(int? CompareToIterationId);
