// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Specifies how the automated reviewer behaves when automatically resolving a comment thread.</summary>
public enum CommentResolutionBehavior
{
    /// <summary>Automatic comment resolution is disabled for this client.</summary>
    Disabled = 0,

    /// <summary>(Default) The thread status is changed to resolved without posting a reply.</summary>
    Silent = 1,

    /// <summary>A reply explaining the resolution is posted before the thread status is changed to resolved.</summary>
    WithReply = 2,
}
