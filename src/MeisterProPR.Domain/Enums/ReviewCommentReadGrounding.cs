// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Whether the reviewer read the actual source at a finding's cited location while producing it.
///     Derived deterministically from the review pass's recorded file-read tool calls — it captures
///     grounding, not correctness: <see cref="Covered" /> means the cited lines were fetched, not that
///     the finding's conclusion is right. Provenance metadata only; it does not participate in
///     deduplication and is <see langword="null" /> when grounding is not applicable (no line number,
///     or a finding produced outside the file-by-file read loop).
/// </summary>
public enum ReviewCommentReadGrounding
{
    // Persisted by ordinal in the review-result jsonb column — keep these values explicit and do NOT
    // reorder or renumber, or historical comment rows would silently remap to a different grounding.

    /// <summary>A file-read covering the cited line was made during the pass and returned that line.</summary>
    Covered = 0,

    /// <summary>No file-read covered the cited line during the pass — the finding is ungrounded.</summary>
    NotRead = 1,

    /// <summary>A covering read was attempted but the cited line is provably absent (empty or beyond end of file).</summary>
    CitedLineMissing = 2,
}
