// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Default in-memory provider driver registry backed by dependency injection.
/// </summary>
/// <remarks>
///     Composition decides what exists: the registry indexes whatever drivers were registered rather than
///     enumerating the provider enum, so adding a family to the enum registers nothing and adding a driver needs
///     no change here.
/// </remarks>
public sealed class AiProviderRegistry(IEnumerable<IAiProviderDriver> drivers) : IAiProviderDriverRegistry
{
    private readonly IReadOnlyDictionary<AiProviderKind, IAiProviderDriver> _drivers = drivers
        .GroupBy(driver => driver.ProviderKind)
        .ToDictionary(group => group.Key, group => group.Last());

    /// <inheritdoc />
    public IReadOnlyList<AiProviderKind> RegisteredKinds => [.. this._drivers.Keys.Order()];

    /// <inheritdoc />
    public bool IsRegistered(AiProviderKind providerKind)
    {
        return this._drivers.ContainsKey(providerKind);
    }

    /// <inheritdoc />
    public IAiProviderDriver GetRequired(AiProviderKind providerKind)
    {
        return this._drivers.TryGetValue(providerKind, out var driver)
            ? driver
            // Names the family and what is available, because the usual cause is a profile configured against a
            // build that has the driver and then run against one that does not.
            : throw new InvalidOperationException(
                $"No AI provider driver is registered for '{providerKind}' in this build "
                + $"(available: {string.Join(", ", this.RegisteredKinds)}).");
    }
}
