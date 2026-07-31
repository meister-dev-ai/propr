// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.CodeInsights;

/// <summary>
///     Resolves the two Code Insights collection gates (the commercial capability and the per-client
///     opt-in) and fails closed.
/// </summary>
/// <remarks>
///     <para>
///         The polarity here is deliberately the opposite of <c>LicensingCapabilityGuard</c>'s. That helper
///         treats an absent licensing service as "unrestricted", which is right for a helper that decides
///         whether to block a user's own action: a misconfigured installation should not lock its operators
///         out. This gate decides whether to start a side effect that spends the customer's model budget and
///         reads their pull-request discussion, so the safe answer when anything is unresolvable is "no".
///     </para>
///     <para>
///         Nothing is cached beyond the call. The edition and the client's opt-in can both change at runtime,
///         and a stale "yes" would keep collecting after someone turned it off.
///     </para>
/// </remarks>
public sealed partial class CodeInsightsCollectionGate(
    IClientRegistry clientRegistry,
    ILogger<CodeInsightsCollectionGate> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ICodeInsightsCollectionGate
{
    public async Task<bool> IsCollectionEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        if (licensingCapabilityService is null)
        {
            // No licensing service means the edition cannot be established, so the commercial gate cannot
            // be shown to be open. Collect nothing.
            LogClosedWithoutLicensing(logger, clientId);
            return false;
        }

        try
        {
            if (!await licensingCapabilityService.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, ct))
            {
                return false;
            }

            return await clientRegistry.GetCodeInsightsCollectionEnabledAsync(clientId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unresolvable gate is a closed gate: collecting on a failed check would spend tokens and
            // store customer discussion without having established the right to.
            LogGateResolutionFailed(logger, clientId, ex);
            return false;
        }
    }
}
