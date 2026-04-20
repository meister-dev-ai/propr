// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Clients.Models;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Evaluates current readiness for one provider connection.</summary>
public interface IProviderReadinessEvaluator
{
    /// <summary>Evaluates current readiness for the supplied client-scoped provider connection.</summary>
    Task<ProviderConnectionReadinessResult> EvaluateAsync(
        Guid clientId,
        ClientScmConnectionDto connection,
        CancellationToken ct = default);
}
