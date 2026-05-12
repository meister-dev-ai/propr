// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Final resolution action applied to an AI-owned thread.
/// </summary>
public sealed record ResolvedThreadAction(
    long ThreadId,
    CommentResolutionBehavior Behavior,
    bool IsResolved,
    string? ReplyText,
    bool ShouldPostReply,
    bool ShouldResolveThread,
    ResolvedThreadReasonSource ReasonSource);

/// <summary>
///     Indicates where a closing explanation originated from.
/// </summary>
public enum ResolvedThreadReasonSource
{
    /// <summary>
    ///     No closing reason is available because the thread remains open.
    /// </summary>
    None,

    /// <summary>
    ///     The closing explanation came directly from the AI evaluation.
    /// </summary>
    AiGenerated,

    /// <summary>
    ///     The closing explanation came from a deterministic fallback.
    /// </summary>
    DeterministicFallback,
}
