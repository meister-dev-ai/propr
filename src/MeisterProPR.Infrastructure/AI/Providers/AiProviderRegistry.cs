// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AI.Providers;

/// <summary>
///     Default in-memory provider driver registry backed by dependency injection.
/// </summary>
public sealed class AiProviderRegistry(IEnumerable<IAiProviderDriver> drivers) : IAiProviderDriverRegistry
{
    private readonly IReadOnlyDictionary<AiProviderKind, IAiProviderDriver> _drivers = drivers
        .GroupBy(driver => driver.ProviderKind)
        .ToDictionary(group => group.Key, group => group.Last());

    public IAiProviderDriver GetRequired(AiProviderKind providerKind)
    {
        return this._drivers.TryGetValue(providerKind, out var driver)
            ? driver
            : throw new InvalidOperationException($"No AI provider driver is registered for '{providerKind}'.");
    }
}
