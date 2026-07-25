// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Which scope layer supplied a resolved catalog value.</summary>
public enum AiModelCatalogLayer
{
    /// <summary>The bundled or imported snapshot, shared by everyone.</summary>
    Global = 0,

    /// <summary>An override held by the client's tenant, typically a negotiated rate.</summary>
    TenantOverride = 1,

    /// <summary>An override held by the client itself, narrower than its tenant's.</summary>
    ClientOverride = 2,
}
