// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     A place in the pull request where a review reported a finding: a file and a line, a file without a line, or the
///     pull request as a whole when both are <see langword="null" />.
/// </summary>
/// <param name="FilePath">The repository-relative path of the file, or <see langword="null" /> for the pull request.</param>
/// <param name="LineNumber">The line within the file, or <see langword="null" /> when the finding names no line.</param>
public sealed record ReviewCommentLocation(string? FilePath, int? LineNumber);
