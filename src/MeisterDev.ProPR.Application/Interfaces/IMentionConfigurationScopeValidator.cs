// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Validates the provider and scope path of a mention configuration before it is stored.
/// </summary>
/// <remarks>
///     Checks three things: that this deployment has active pull-request discovery for the provider, that it
///     has a reply publisher for the provider, and that the scope path matches something the client has
///     already configured. The provider policy defines whether a saved enabled scope or an active connection
///     satisfies that selection.
/// </remarks>
public interface IMentionConfigurationScopeValidator
{
    /// <summary>Validates a provider and scope path against what the client has configured.</summary>
    /// <param name="clientId">The client the configuration belongs to.</param>
    /// <param name="provider">The provider family named in the request.</param>
    /// <param name="scopePath">The scope path named in the request.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MentionScopeVerdict> ValidateAsync(
        Guid clientId,
        ScmProvider provider,
        string scopePath,
        CancellationToken ct = default);
}
