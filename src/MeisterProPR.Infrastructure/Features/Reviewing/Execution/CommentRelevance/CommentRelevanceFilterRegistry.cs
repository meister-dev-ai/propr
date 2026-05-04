// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

public sealed class CommentRelevanceFilterRegistry(
    IEnumerable<ICommentRelevanceFilter> filters,
    CommentRelevanceFilterSelection selection)
{
    private readonly IReadOnlyDictionary<string, ICommentRelevanceFilter> _filters = filters
        .GroupBy(filter => filter.ImplementationId, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    public CommentRelevanceFilterSelection Selection { get; } = selection;

    public bool HasSelection => this.Selection.HasSelection;

    public bool TryResolveSelected(out ICommentRelevanceFilter? filter)
    {
        filter = null;
        if (!this.Selection.HasSelection)
        {
            return false;
        }

        return this._filters.TryGetValue(this.Selection.SelectedImplementationId!, out filter);
    }
}
