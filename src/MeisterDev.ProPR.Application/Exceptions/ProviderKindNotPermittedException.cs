// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.Exceptions;

/// <summary>
///     Thrown when a connection profile would use a provider family its tenant does not permit. Raised at
///     configuration time so the refusal arrives while an operator is looking at the form, rather than mid-review.
/// </summary>
public sealed class ProviderKindNotPermittedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderKindNotPermittedException" /> class.</summary>
    /// <param name="providerKind">The provider family that was refused.</param>
    /// <param name="reason">Why it was refused, including what is permitted instead.</param>
    public ProviderKindNotPermittedException(AiProviderKind providerKind, string reason)
        : base($"The connection profile cannot be saved: {reason}.")
    {
        this.ProviderKind = providerKind;
    }

    /// <summary>The provider family that was refused.</summary>
    public AiProviderKind ProviderKind { get; }
}
