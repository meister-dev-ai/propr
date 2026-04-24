// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Application.Features.Licensing.Ports;

/// <summary>Read-only catalog for known premium capabilities.</summary>
public interface IPremiumCapabilityCatalog
{
    /// <summary>Returns all known capabilities in display order.</summary>
    IReadOnlyList<PremiumCapabilityDefinition> GetAll();

    /// <summary>Returns the capability definition for <paramref name="capabilityKey" />, or <see langword="null" />.</summary>
    PremiumCapabilityDefinition? Get(string capabilityKey);
}
