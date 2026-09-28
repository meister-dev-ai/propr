// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Provider-neutral publication context carried from orchestration into provider adapters.
/// </summary>
/// <param name="Review">The pull request the result belongs to.</param>
/// <param name="Revision">The revision the result was produced against.</param>
/// <param name="AuthorizedPublicationIdentity">The identity the comments are attributed to.</param>
/// <param name="ExistingThreads">The threads already on the pull request.</param>
/// <param name="ProviderSpecificContext">Facts only one provider needs, read back by type.</param>
/// <param name="ReplyInExistingSummaryThread">
///     Whether a summary belongs inside a bot summary thread that already exists, as a reply. A review result
///     leaves that thread alone, because its summary was already written there. An admission refusal sets this:
///     its whole message is the summary, and skipping it would leave the author with no notice at all.
///     It applies where a provider skips a summary it has already posted, today Azure DevOps. The other
///     providers post the refusal as a new comment whatever this says.
/// </param>
public sealed record ReviewPublicationContext(
    CodeReviewRef Review,
    ReviewRevision Revision,
    ReviewerIdentity AuthorizedPublicationIdentity,
    IReadOnlyList<PrCommentThread> ExistingThreads,
    object? ProviderSpecificContext = null,
    bool ReplyInExistingSummaryThread = false)
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
