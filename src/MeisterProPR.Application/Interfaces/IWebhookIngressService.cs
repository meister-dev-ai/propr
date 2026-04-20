// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Verifies and normalizes raw webhook deliveries for one provider family.</summary>
public interface IWebhookIngressService
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Verifies whether the raw delivery is authentic for the provider configuration.</summary>
    Task<bool> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        string? verificationSecret = null,
        CancellationToken ct = default);

    /// <summary>Parses the raw delivery into a normalized webhook envelope.</summary>
    Task<WebhookDeliveryEnvelope> ParseAsync(
        Guid clientId,
        ProviderHostRef host,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        CancellationToken ct = default);
}
