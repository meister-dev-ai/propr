// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Publication-ready anchor state for a single review comment.
/// </summary>
public sealed record PublicationAnchorContext(
    string? FilePath,
    int? RequestedLineNumber,
    string? NormalizedFilePath,
    int? ResolvedLineNumber,
    PublicationAnchorPrecision AnchorPrecision,
    string? ProviderTrackingReference = null,
    string? CompareRevisionReference = null);

/// <summary>
///     Precision level that can be trusted for a provider-native comment anchor.
/// </summary>
public enum PublicationAnchorPrecision
{
    Inline,
    File,
    PrLevel,
}
