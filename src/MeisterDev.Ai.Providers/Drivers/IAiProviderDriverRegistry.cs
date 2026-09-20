// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Registry for provider-specific AI drivers.
/// </summary>
/// <remarks>
///     The registry is the authority on which provider families this build can call, and on which identities
///     exist at all: a family is a loaded driver and nothing else, so the set of identities is whatever was
///     composed rather than a set compiled into the host. A host asks here before offering a family to an
///     operator, otherwise a profile could be configured against a family that fails only once a review runs.
/// </remarks>
public interface IAiProviderDriverRegistry
{
    /// <summary>The identity keys of the families a driver is registered for, in ordinal order.</summary>
    IReadOnlyList<string> RegisteredKinds { get; }

    /// <summary>Whether this build can call <paramref name="providerKind" />.</summary>
    /// <param name="providerKind">
    ///     The provider family to check, named by a family's declared identity key or by a key that family
    ///     supersedes. Case is folded, as it is everywhere a key is compared.
    /// </param>
    bool IsRegistered(string? providerKind);

    /// <summary>Gets the driver for the requested provider family.</summary>
    /// <param name="providerKind">
    ///     The provider family to resolve, named by its declared identity key or by a key it supersedes.
    /// </param>
    IAiProviderDriver GetRequired(string? providerKind);
}

/// <summary>Reads of a driver registry that more than one caller makes.</summary>
public static class AiProviderDriverRegistryExtensions
{
    /// <summary>
    ///     The hosts <paramref name="providerKind" /> declared it reaches, or none where no driver serves it.
    /// </summary>
    /// <param name="registry">The registry to read.</param>
    /// <param name="providerKind">The provider family to read.</param>
    /// <remarks>
    ///     Read through one method rather than from each caller's own lookup, so a tenant's endpoint restriction is
    ///     checked against the same set wherever it is enforced. A family no driver serves declares nothing the
    ///     host can read; a connection on one is refused for being unserviceable before it reaches a provider.
    /// </remarks>
    public static IReadOnlyList<string> ReachedHostPatterns(
        this IAiProviderDriverRegistry registry,
        string? providerKind)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.IsRegistered(providerKind)
            ? registry.GetRequired(providerKind).Declaration.ReachedHostPatterns
            : [];
    }
}
