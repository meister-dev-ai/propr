// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Application.Exceptions;

/// <summary>Thrown when a premium capability is required for the requested operation.</summary>
public sealed class PremiumFeatureUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PremiumFeatureUnavailableException" /> class.</summary>
    public PremiumFeatureUnavailableException(CapabilitySnapshot capability)
        : base(capability.Message ?? $"Capability '{capability.Key}' is unavailable.")
    {
        this.Capability = capability;
    }

    /// <summary>Gets the resolved capability snapshot that caused the operation to be rejected.</summary>
    public CapabilitySnapshot Capability { get; }
}
