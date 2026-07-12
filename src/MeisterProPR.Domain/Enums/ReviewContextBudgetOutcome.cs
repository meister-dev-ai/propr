// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     How pre-flight context-window budgeting treated a file's review call.
/// </summary>
public enum ReviewContextBudgetOutcome
{
    /// <summary>The payload fit the model's context window (with or without in-loop tool-history trimming).</summary>
    Normal = 0,

    /// <summary>
    ///     The file-content windows were dropped so the file was reviewed diff-only to stay within the window.
    /// </summary>
    DegradedDiffOnly = 1,

    /// <summary>
    ///     Even the minimal payload (system prompt + diff) exceeded the window, so the file was skipped
    ///     without a provider call.
    /// </summary>
    Skipped = 2,
}
