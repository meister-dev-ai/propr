// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     A per-file unified diff to retain for a specific review increment. The
///     <paramref name="UnifiedDiff" /> is supplied in plaintext and is encrypted by the store on write.
/// </summary>
/// <param name="FilePath">The file path the diff applies to.</param>
/// <param name="ChangeType">The kind of change, e.g. "add", "edit", "delete", "rename".</param>
/// <param name="IsBinary">Whether the file is binary.</param>
/// <param name="UnifiedDiff">The plaintext canonical unified diff (encrypted at rest by the store).</param>
public sealed record RetainedFileDiffSnapshot(
    string FilePath,
    string ChangeType,
    bool IsBinary,
    string UnifiedDiff);
