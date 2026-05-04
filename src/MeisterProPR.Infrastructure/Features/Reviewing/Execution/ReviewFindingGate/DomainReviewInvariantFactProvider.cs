// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

/// <summary>
///     Curated Reviewing-owned domain invariant facts.
/// </summary>
public sealed class DomainReviewInvariantFactProvider : IReviewInvariantFactProvider
{
    public const string ReviewCommentMessageRequiredInvariantId = InvariantFact.ReviewCommentMessageRequiredInvariantId;
    public const string ReviewResultCommentsRequiredInvariantId = InvariantFact.ReviewResultCommentsRequiredInvariantId;

    public IReadOnlyList<InvariantFact> GetFacts()
    {
        return
        [
            new InvariantFact(
                ReviewCommentMessageRequiredInvariantId,
                InvariantFact.DomainFamily,
                "ReviewComment.Message required",
                "ReviewComment constructor semantics",
                "message_non_null_and_non_empty",
                "ReviewComment requires a non-null, non-empty message value."),
            new InvariantFact(
                ReviewResultCommentsRequiredInvariantId,
                InvariantFact.DomainFamily,
                "ReviewResult.Comments required",
                "ReviewResult constructor semantics",
                "comments_collection_non_null",
                "ReviewResult requires a non-null comments collection value."),
        ];
    }
}
