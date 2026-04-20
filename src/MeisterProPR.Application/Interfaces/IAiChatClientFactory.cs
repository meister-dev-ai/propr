// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Factory for creating <see cref="IChatClient" /> instances from per-client AI connection details.</summary>
public interface IAiChatClientFactory
{
    /// <summary>
    ///     Creates an <see cref="IChatClient" /> for the given endpoint.
    /// </summary>
    /// <param name="endpointUrl">The Azure OpenAI or AI Foundry endpoint URL.</param>
    /// <param name="apiKey">Optional API key. When null <c>DefaultAzureCredential</c> is used.</param>
    /// <returns>A configured <see cref="IChatClient" /> instance.</returns>
    IChatClient CreateClient(string endpointUrl, string? apiKey);

    /// <summary>
    ///     Probes the given endpoint and returns the names of available model deployments.
    ///     Returns an empty list when the endpoint is unreachable or returns no deployments.
    /// </summary>
    /// <param name="endpointUrl">The Azure OpenAI or AI Foundry endpoint URL.</param>
    /// <param name="apiKey">API key used to authenticate the probe request.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ProbeDeploymentsAsync(
        string endpointUrl,
        string apiKey,
        CancellationToken ct = default);
}
