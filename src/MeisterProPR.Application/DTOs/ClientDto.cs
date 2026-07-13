// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Carries client data across the Application/Infrastructure boundary. The secret key and ADO client secret are
///     never included.
/// </summary>
public sealed record ClientDto(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    CommentResolutionBehavior CommentResolutionBehavior,
    string? CustomSystemMessage,
    string? DefaultReviewPipelineProfileId,
    DateTimeOffset? DefaultReviewPipelineProfileUpdatedAtUtc,
    bool ScmCommentPostingEnabled,
    bool EnableEvidenceBackedVerification = false,
    bool EnableLanguageRobustScreening = false,
    bool EnableMultiPassUnion = false,
    bool IncludeLinkedItemsInContext = true,
    IReadOnlyList<ReviewPassDto>? ReviewPasses = null,
    Guid? TenantId = null,
    string? TenantSlug = null,
    string? TenantDisplayName = null)
{
    /// <summary>The ordered review-pass list, or an empty list when none are configured.</summary>
    public IReadOnlyList<ReviewPassDto> ReviewPassesOrEmpty => this.ReviewPasses ?? [];
}

/// <summary>
///     One entry in a client's ordered review-pass list: an additional multi-pass union pass bound to a configured
///     model, with an optional specialist lens, an optional scope, and a shadow flag. <see cref="Ordinal" /> is the
///     zero-based position after the implicit tier baseline pass; <see cref="Lens" /> is <see langword="null" /> for an
///     ordinary resample pass; <see cref="Scope" /> is <see langword="null" /> for the per-file default; <see
///     cref="Shadow" /> is additive metadata the runtime does not act on yet.
/// </summary>
public sealed record ReviewPassDto(int Ordinal, Guid ConfiguredModelId, string? Lens = null, string? Scope = null, bool Shadow = false);
