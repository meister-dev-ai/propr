// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Registry for provider-specific AI drivers.
/// </summary>
/// <remarks>
///     The registry — not the enum — is the authority on which provider families this build can actually call.
///     <see cref="AiProviderKind" /> deliberately names families ahead of their drivers, so a host asks here
///     before offering one to an operator; otherwise opening the enum would let someone configure a profile that
///     fails only once a review runs.
/// </remarks>
public interface IAiProviderDriverRegistry
{
    /// <summary>The provider families a driver is registered for, in enum order.</summary>
    IReadOnlyList<AiProviderKind> RegisteredKinds { get; }

    /// <summary>Whether this build can call <paramref name="providerKind" />.</summary>
    /// <param name="providerKind">The provider family to check.</param>
    bool IsRegistered(AiProviderKind providerKind);

    /// <summary>Gets the driver for the requested provider family.</summary>
    /// <param name="providerKind">The provider family to resolve.</param>
    IAiProviderDriver GetRequired(AiProviderKind providerKind);
}
