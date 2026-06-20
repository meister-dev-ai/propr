// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     A 1-based inclusive changed line range in the new (post-diff) file, sourced from
///     <c>ReviewDiffProcessor.ExtractChangedNewLineRanges</c>.
/// </summary>
public readonly record struct ChangedLineRange(int Start, int End)
{
    /// <summary>True when the range covers no lines.</summary>
    public bool IsEmpty => this.End < this.Start;
}
