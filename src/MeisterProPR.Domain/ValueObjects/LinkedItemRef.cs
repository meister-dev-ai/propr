// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     A reference from one linked item to another item (a related, parent, or child work item, or a
///     cross-referenced issue). Resolvable on demand through the linked-item review tools.
/// </summary>
/// <param name="Kind">
///     Relationship label as reported by the provider (e.g. "related", "parent", "child", "duplicate").
/// </param>
/// <param name="TargetKey">Provider-native identifier of the referenced item.</param>
/// <param name="Url">Optional canonical URL of the referenced item.</param>
/// <param name="Title">Optional title of the referenced item when the provider returns it cheaply.</param>
public sealed record LinkedItemRef(string Kind, string TargetKey, string? Url = null, string? Title = null);
