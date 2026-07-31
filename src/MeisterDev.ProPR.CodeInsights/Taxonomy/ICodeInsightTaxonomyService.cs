// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Taxonomy;

/// <summary>
///     The source of truth for the finding-type vocabulary: the installation's fixed core set plus each
///     client's custom tags. The classifier assigns from what this returns, and the roll-ups compare on the
///     core part of it.
/// </summary>
public interface ICodeInsightTaxonomyService
{
    /// <summary>
    ///     Returns the full vocabulary for <paramref name="clientId" />: the fixed core set plus the client's
    ///     custom tags. Retired custom tags are included, so a historical assignment always resolves to a
    ///     name; callers offering tags for new assignment must filter on the retirement timestamp.
    /// </summary>
    Task<CodeInsightTaxonomyDto> GetTaxonomyAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the vocabulary a classifier may assign from for <paramref name="clientId" />: the fixed
    ///     core set plus only the client's active custom tags.
    /// </summary>
    Task<CodeInsightTaxonomyDto> GetAssignableTaxonomyAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Creates a custom tag for <paramref name="clientId" />. Rejects a slug that is malformed, that
    ///     shadows a core type, or that the client already uses, including by a retired tag, because reusing
    ///     a retired slug would make one historical label mean two things.
    /// </summary>
    Task<CodeInsightCustomTagWriteResult> CreateCustomTagAsync(
        Guid clientId,
        CodeInsightCustomTagWriteRequest request,
        CancellationToken ct = default);

    /// <summary>
    ///     Updates a custom tag's slug, display name, and definition. Existing assignments are untouched:
    ///     they reference the tag's identity, so a rename never orphans or relabels a historical finding.
    /// </summary>
    Task<CodeInsightCustomTagWriteResult> UpdateCustomTagAsync(
        Guid clientId,
        Guid tagId,
        CodeInsightCustomTagWriteRequest request,
        CancellationToken ct = default);

    /// <summary>
    ///     Retires a custom tag: it stops being offered for new assignments while every finding that already
    ///     carries it keeps resolving. Retiring an already-retired tag is a no-op success.
    /// </summary>
    Task<CodeInsightCustomTagWriteResult> RetireCustomTagAsync(
        Guid clientId,
        Guid tagId,
        CancellationToken ct = default);
}
