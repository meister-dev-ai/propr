// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Lightweight identity of a client within a tenant, used to populate member client-access pickers.</summary>
public sealed record TenantClientSummaryDto(
    Guid Id,
    string DisplayName,
    bool IsActive);
