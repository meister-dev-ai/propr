// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AI.Providers;

/// <summary>
///     Registry for provider-specific AI drivers.
/// </summary>
public interface IAiProviderDriverRegistry
{
    /// <summary>Gets the driver for the requested provider family.</summary>
    IAiProviderDriver GetRequired(AiProviderKind providerKind);
}
