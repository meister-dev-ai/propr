// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     A work item (Azure DevOps) or issue (GitHub, GitLab, Forgejo) linked to a pull request,
///     summarized for inclusion in the review context. Provider-neutral: every SCM adapter maps
///     its native linked-item shape onto this record.
/// </summary>
/// <param name="ProviderKey">
///     Provider-native identifier of the item (an Azure DevOps work-item id, or a GitHub/GitLab/Forgejo
///     issue number), stable enough to resolve the item again through the owning provider.
/// </param>
/// <param name="ItemType">
///     Provider-native item type label (e.g. "Bug", "User Story", "Issue"), or a neutral fallback
///     ("WorkItem" / "Issue") when the provider exposes none.
/// </param>
/// <param name="Title">Item title.</param>
/// <param name="Description">
///     Optional item description or body. Bounded (item count and per-item length) before it reaches
///     the prompt; see the eager attach step.
/// </param>
/// <param name="Url">Optional canonical URL of the item.</param>
/// <param name="RelatedLinks">
///     Other items this item links to (related/parent/child work items, cross-referenced issues).
///     Resolvable on demand through the linked-item review tools. Never <c>null</c>; empty when none.
/// </param>
public sealed record LinkedItem(
    string ProviderKey,
    string ItemType,
    string Title,
    string? Description,
    string? Url,
    IReadOnlyList<LinkedItemRef> RelatedLinks);
