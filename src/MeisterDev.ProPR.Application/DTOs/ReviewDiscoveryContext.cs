// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Validated connection and saved provider scope for manual review discovery.</summary>
/// <param name="ConnectionId">Connection validated for the owning client and repository target.</param>
/// <param name="ProviderScopePath">Saved provider target scope.</param>
public sealed record ReviewDiscoveryContext(Guid ConnectionId, string ProviderScopePath);
