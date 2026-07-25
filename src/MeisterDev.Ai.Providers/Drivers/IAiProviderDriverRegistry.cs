// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Registry for provider-specific AI drivers.
/// </summary>
public interface IAiProviderDriverRegistry
{
    /// <summary>Gets the driver for the requested provider family.</summary>
    IAiProviderDriver GetRequired(AiProviderKind providerKind);
}
