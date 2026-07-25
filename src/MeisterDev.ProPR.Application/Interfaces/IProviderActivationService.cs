// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Resolves and updates installation-wide provider-family activation policy.</summary>
public interface IProviderActivationService
{
    /// <summary>Returns the activation status for every provider family.</summary>
    Task<IReadOnlyList<ProviderActivationStatusDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Sets whether the given provider family is enabled for the installation.</summary>
    Task<ProviderActivationStatusDto> SetEnabledAsync(
        ScmProvider provider,
        bool isEnabled,
        CancellationToken ct = default);

    /// <summary>Returns whether the given provider family is enabled for the installation.</summary>
    Task<bool> IsEnabledAsync(ScmProvider provider, CancellationToken ct = default);

    /// <summary>Returns the currently enabled provider families.</summary>
    Task<IReadOnlySet<ScmProvider>> GetEnabledProvidersAsync(CancellationToken ct = default);
}
