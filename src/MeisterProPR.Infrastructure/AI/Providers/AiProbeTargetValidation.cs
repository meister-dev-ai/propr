// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Networking;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AI.Providers;

/// <summary>
///     Shared base-URL / SSRF-egress / auth-shape validation used by the AI provider drivers. Keeping it here
///     (Infrastructure) rather than in the controller means provider-specific rules live behind the
///     <c>IAiProviderDriver</c> seam, not in the API layer.
/// </summary>
internal static class AiProbeTargetValidation
{
    /// <summary>Validates an OpenAI-compatible target (plain OpenAI or LiteLLM).</summary>
    /// <param name="target">The probe target.</param>
    /// <param name="allowPrivateEgress">When true, a private/loopback/link-local host is permitted so a self-hosted / on-prem endpoint can be configured (Development, or the operator opt-in).</param>
    /// <param name="allowInsecureScheme">When true (Development only), a plain-http baseUrl is permitted so a local provider stays reachable. The private-egress opt-in does not relax this.</param>
    /// <param name="rejectAzureHosts">When true (plain OpenAI), an Azure-hosted URL is rejected so it is configured under the Azure provider kind instead.</param>
    public static string? ForOpenAiCompatible(AiProbeTarget target, bool allowPrivateEgress, bool allowInsecureScheme, bool rejectAzureHosts)
    {
        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
        {
            return "baseUrl must be an absolute URL.";
        }

        if (rejectAzureHosts && AzureAiHostPolicy.IsAzureAiHost(uri.Host))
        {
            return "Azure-hosted OpenAI endpoints, including Azure AI Foundry OpenAI endpoints, must use providerKind 'azureOpenAi' instead of 'openAi'.";
        }

        var egressError = ValidateEgress(uri, allowPrivateEgress, allowInsecureScheme);
        if (egressError is not null)
        {
            return egressError;
        }

        return RequireApiKey(target);
    }

    /// <summary>Validates an Azure OpenAI target: the host is locked to Azure AI hosts (the Azure SDK bypasses the connect-time egress guard).</summary>
    /// <param name="target">The probe target.</param>
    public static string? ForAzureOpenAi(AiProbeTarget target)
    {
        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
        {
            return "baseUrl must be an absolute URL.";
        }

        if (!AzureAiHostPolicy.IsAzureAiHost(uri.Host))
        {
            return "Azure OpenAI connections must target an Azure AI host (*.openai.azure.com, *.services.ai.azure.com, or *.cognitiveservices.azure.com).";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "baseUrl must use https.";
        }

        // Azure supports managed-identity auth (no API key); other modes require an API key.
        if (target.AuthMode == AiAuthMode.AzureIdentity)
        {
            return null;
        }

        return RequireApiKey(target, "An API key or Azure identity is required for this provider.");
    }

    private static string? ValidateEgress(Uri uri, bool allowPrivateEgress, bool allowInsecureScheme)
    {
        // https is required unless a Development-local provider needs plain http. The private-egress opt-in
        // intentionally does NOT relax the scheme — a self-hosted / on-prem endpoint must still use https.
        if (!allowInsecureScheme
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "baseUrl must use https.";
        }

        // The private/loopback/link-local block is lifted only when private egress is permitted — Development,
        // or the operator opt-in — so an on-prem endpoint can be configured. It stays blocked by default.
        if (!allowPrivateEgress && EgressAddressPolicy.IsBlockedEgressHost(uri.Host))
        {
            return "baseUrl must not target a private, loopback, or link-local address.";
        }

        return null;
    }

    private static string? RequireApiKey(AiProbeTarget target, string? message = null)
    {
        if (target.AuthMode != AiAuthMode.ApiKey || !target.HasApiKey)
        {
            return message ?? "An API key is required for this provider and auth mode.";
        }

        return null;
    }
}
