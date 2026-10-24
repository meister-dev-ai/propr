// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>An optional replacement for a contiguous range in the reviewed file.</summary>
public sealed record ReviewSuggestedFix(
    int StartLine,
    int EndLine,
    string OriginalText,
    string ReplacementText,
    string? SourceHeadSha = null,
    string? SourceFilePath = null,
    bool IsSourceValidated = false,
    int? SourceAnchorLineNumber = null,
    bool IsAnchorRemapped = false,
    string? RejectionReason = null);
