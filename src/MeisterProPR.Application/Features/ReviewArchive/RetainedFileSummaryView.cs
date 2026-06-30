// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     A lightweight listing of a retained file under a pull request, without the (encrypted) diff text.
///     One entry represents the newest retained revision for a given file path.
/// </summary>
/// <param name="FilePath">The file path the diff applies to.</param>
/// <param name="RevisionKey">The newest retained review increment the file belongs to.</param>
/// <param name="ChangeType">The kind of change.</param>
/// <param name="IsBinary">Whether the file is binary (and therefore has no renderable diff).</param>
/// <param name="CreatedAt">UTC timestamp the newest retained diff for the file was captured.</param>
public sealed record RetainedFileSummaryView(
    string FilePath,
    string RevisionKey,
    string ChangeType,
    bool IsBinary,
    DateTimeOffset CreatedAt);
