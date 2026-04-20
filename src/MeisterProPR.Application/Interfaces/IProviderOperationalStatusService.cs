// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Builds operator-facing provider operational status views from the readiness model.</summary>
public interface IProviderOperationalStatusService
{
    /// <summary>Returns provider operational status for one client.</summary>
    Task<ProviderOperationalStatusDto> GetForClientAsync(Guid clientId, CancellationToken ct = default);
}
