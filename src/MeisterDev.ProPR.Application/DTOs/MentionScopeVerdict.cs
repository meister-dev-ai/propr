// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Why a mention configuration's provider and scope path were turned away, if they were.</summary>
public enum MentionScopeRefusal
{
    /// <summary>Nothing was wrong with it.</summary>
    None = 0,

    /// <summary>This deployment has no pull-request discovery for that provider, so it could never scan.</summary>
    UnsupportedProvider = 1,

    /// <summary>The scope path matches no enabled connection the client holds for that provider.</summary>
    UnknownScopePath = 2,

    /// <summary>
    ///     The provider offers no way to publish a reply into a review thread, so a question found there could
    ///     never be answered where it was asked.
    /// </summary>
    CannotPublishReplies = 3,
}

/// <summary>The answer to whether a mention configuration names somewhere this client can be scanned.</summary>
/// <param name="Refusal">Why it was turned away, or <see cref="MentionScopeRefusal.None" />.</param>
/// <param name="Message">What to tell the caller. Empty when nothing was wrong.</param>
public sealed record MentionScopeVerdict(MentionScopeRefusal Refusal, string Message)
{
    /// <summary>The verdict for a provider and scope path the client can be scanned on.</summary>
    public static MentionScopeVerdict Accepted { get; } = new(MentionScopeRefusal.None, string.Empty);

    /// <summary>Whether the configuration may be written.</summary>
    public bool IsAccepted => this.Refusal == MentionScopeRefusal.None;
}
