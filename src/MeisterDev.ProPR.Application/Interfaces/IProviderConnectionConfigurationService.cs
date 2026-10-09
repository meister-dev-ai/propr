// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Clients.Models;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Coordinates native connection validation and verification.</summary>
public interface IProviderConnectionConfigurationService
{
    IReadOnlyList<(string PropertyName, string Message)> Validate(ScmAuthenticationConfiguration configuration);

    Task VerifyAsync(ClientScmConnectionDto connection, CancellationToken ct = default);
}
