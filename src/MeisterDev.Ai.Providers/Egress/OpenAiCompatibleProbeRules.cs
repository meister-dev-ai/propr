// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     The probe rules shared by the drivers that speak the OpenAI protocol: OpenAI itself, LiteLLM, and any
///     other endpoint serving that wire format.
/// </summary>
/// <remarks>
///     Shared because it is one protocol with one set of rules, not because it is convenient: three drivers
///     differ in what they point at, not in what makes a target valid. A driver with rules of its own keeps them
///     in its own file.
/// </remarks>
internal static class OpenAiCompatibleProbeRules
{
    /// <summary>Validates a target that speaks the OpenAI protocol.</summary>
    /// <param name="target">The probe target.</param>
    /// <param name="allowPrivateEgress">When true, a private, loopback, or link-local host is permitted.</param>
    /// <param name="allowInsecureScheme">When true (Development only), a plain-http baseUrl is permitted.</param>
    /// <param name="rejectAzureHosts">
    ///     When true, an Azure-hosted URL is refused so it is configured under the Azure provider kind, which
    ///     authenticates differently and can use a managed identity instead of a key.
    /// </param>
    /// <returns>An error to report, or <see langword="null" /> when the target is usable.</returns>
    public static string? Validate(
        AiProbeTarget target,
        bool allowPrivateEgress,
        bool allowInsecureScheme,
        bool rejectAzureHosts)
    {
        if (ProbeTargetChecks.AbsoluteUrl(target, out var uri) is { } urlError)
        {
            return urlError;
        }

        if (rejectAzureHosts && AzureAiHostPolicy.IsAzureAiHost(uri.Host))
        {
            return "Azure-hosted OpenAI endpoints, including Azure AI Foundry OpenAI endpoints, must use "
                   + "providerKind 'azureOpenAi' instead of 'openAi'.";
        }

        return ProbeTargetChecks.Egress(uri, allowPrivateEgress, allowInsecureScheme)
               ?? ProbeTargetChecks.RequireApiKey(target);
    }
}
