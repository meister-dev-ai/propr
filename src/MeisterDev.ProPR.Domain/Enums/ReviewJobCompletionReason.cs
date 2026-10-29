// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Why a completed review job ended before it had reviewed everything in scope. A completed job keeps its
///     summary and its findings, and this value records the reason, which a reader would otherwise have to find in
///     the summary text.
/// </summary>
public enum ReviewJobCompletionReason
{
    /// <summary>
    ///     No recorded reason. The review either reviewed every file in scope or ended early for a reason that is
    ///     recorded elsewhere, such as the budget block of a per-increment soft cap.
    /// </summary>
    None = 0,

    /// <summary>
    ///     The review reached the maximum review duration of its tenant. It started no further files or
    ///     pull-request-wide passes after that point and completed with the files it had reviewed.
    /// </summary>
    TimeLimitReached = 1,
}
