// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     The structured detail of a single linked item, retrieved on demand when the review model asks
///     for more than the eager summary. Provider-neutral: field names are the provider's own labels
///     (e.g. "State", "Acceptance Criteria", "Assigned To") mapped into a flat dictionary.
/// </summary>
/// <param name="ProviderKey">Provider-native identifier of the item.</param>
/// <param name="ItemType">Provider-native item type label.</param>
/// <param name="Title">Item title.</param>
/// <param name="Description">Optional full item description or body (untruncated for on-demand reads).</param>
/// <param name="State">Optional workflow state (e.g. "Active", "Open", "Closed").</param>
/// <param name="Fields">
///     Additional structured fields keyed by the provider's own field label. Never <c>null</c>; empty
///     when the provider exposes none.
/// </param>
/// <param name="RelatedLinks">Related links surfaced on the item. Never <c>null</c>; empty when none.</param>
public sealed record LinkedItemDetails(
    string ProviderKey,
    string ItemType,
    string Title,
    string? Description,
    string? State,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<LinkedItemRef> RelatedLinks);
