// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

/// <summary>
///     Maps the Application-layer <see cref="ChangedLineRelation" /> classification onto the Domain-layer
///     <see cref="ReviewCommentScopeRelation" /> carried on a published review comment. A <see langword="null" />
///     relation maps to <see langword="null" /> so unclassified findings are never labeled.
/// </summary>
internal static class ReviewCommentScopeRelationMapper
{
    /// <summary>
    ///     Maps a finding-scope classification onto the value persisted on a review comment.
    /// </summary>
    public static ReviewCommentScopeRelation? Map(ChangedLineRelation? relation)
    {
        return relation switch
        {
            ChangedLineRelation.OnChangedLine => ReviewCommentScopeRelation.OnChangedLine,
            ChangedLineRelation.AdjacentToChange => ReviewCommentScopeRelation.AdjacentToChange,
            ChangedLineRelation.OutsideChange => ReviewCommentScopeRelation.OutsideChange,
            _ => null,
        };
    }
}
