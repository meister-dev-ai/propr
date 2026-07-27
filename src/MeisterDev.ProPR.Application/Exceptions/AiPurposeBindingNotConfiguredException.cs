// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Exceptions;

/// <summary>
///     Thrown when a client has nothing mapped to an AI purpose, so no runtime can be built for it.
/// </summary>
/// <remarks>
///     A distinct type because callers act on this case rather than merely reporting it: an optional purpose degrades
///     to non-AI behaviour instead of failing the review. Those callers previously caught every exception, which meant
///     an infrastructure fault reaching them was reported as an unmapped purpose and the real cause was lost. Derives
///     from <see cref="InvalidOperationException" /> so callers that only care that resolution failed keep working.
/// </remarks>
public sealed class AiPurposeBindingNotConfiguredException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AiPurposeBindingNotConfiguredException" /> class.</summary>
    /// <param name="purpose">The purpose that has no active binding.</param>
    public AiPurposeBindingNotConfiguredException(AiPurpose purpose)
        : base($"No active AI binding is configured for purpose '{purpose}'.")
    {
        this.Purpose = purpose;
    }

    /// <summary>Gets the purpose that has no active binding.</summary>
    public AiPurpose Purpose { get; }
}
