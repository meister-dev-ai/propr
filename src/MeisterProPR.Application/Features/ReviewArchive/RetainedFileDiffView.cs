// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     A retained per-file diff as returned from the store, with its unified diff already decrypted.
/// </summary>
/// <param name="RevisionKey">The review increment the diff belongs to.</param>
/// <param name="FilePath">The file path the diff applies to.</param>
/// <param name="ChangeType">The kind of change.</param>
/// <param name="IsBinary">Whether the file is binary.</param>
/// <param name="UnifiedDiff">The decrypted canonical unified diff.</param>
/// <param name="CreatedAt">UTC timestamp the diff was retained.</param>
public sealed record RetainedFileDiffView(
    string RevisionKey,
    string FilePath,
    string ChangeType,
    bool IsBinary,
    string UnifiedDiff,
    DateTimeOffset CreatedAt);
