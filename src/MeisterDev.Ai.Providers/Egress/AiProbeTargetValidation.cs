// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     Shared base-URL / SSRF-egress / auth-shape validation used by the AI provider drivers.
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

    /// <summary>
    ///     Validates an Anthropic target. The host is deliberately not pinned to Anthropic's own domain — the
    ///     Messages protocol is also served by gateways and enterprise proxies — so what is checked is the egress
    ///     policy and that a key is present in the header mode Anthropic reads it from.
    /// </summary>
    /// <param name="target">The probe target.</param>
    /// <param name="allowPrivateEgress">When true, a private/loopback/link-local host is permitted.</param>
    /// <param name="allowInsecureScheme">When true (Development only), a plain-http baseUrl is permitted.</param>
    public static string? ForAnthropic(AiProbeTarget target, bool allowPrivateEgress, bool allowInsecureScheme)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
        {
            return "baseUrl must be an absolute URL.";
        }

        var egressError = ValidateEgress(uri, allowPrivateEgress, allowInsecureScheme);
        if (egressError is not null)
        {
            return egressError;
        }

        // Anthropic rejects a bearer token and reads x-api-key, so a profile configured for bearer auth would
        // fail on its first call. Saying so here beats a 401 an operator has to interpret.
        if (target.AuthMode != AiAuthMode.XApiKey && target.AuthMode != AiAuthMode.ApiKey)
        {
            return "Anthropic authenticates with an API key sent as 'x-api-key'; choose that authentication mode.";
        }

        return target.HasApiKey ? null : "An API key is required for Anthropic.";
    }

    /// <summary>
    ///     Validates an AWS Bedrock target. The endpoint has to name its region, because that is where the
    ///     inference happens and a profile whose region is implicit cannot be checked against a residency
    ///     requirement.
    /// </summary>
    /// <param name="target">The probe target.</param>
    /// <param name="allowPrivateEgress">When true, a private or VPC endpoint outside AWS's own hosts is permitted.</param>
    /// <param name="allowInsecureScheme">When true (Development only), a plain-http baseUrl is permitted.</param>
    public static string? ForAwsBedrock(AiProbeTarget target, bool allowPrivateEgress, bool allowInsecureScheme)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
        {
            return "baseUrl must be an absolute URL.";
        }

        var egressError = ValidateEgress(uri, allowPrivateEgress, allowInsecureScheme);
        if (egressError is not null)
        {
            return egressError;
        }

        // Anything that is not an AWS host is a private or VPC endpoint, which is the operator opt-in's business
        // rather than the default. On an AWS host the region is read from the host itself.
        var isAwsHost = uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
                        || uri.Host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase);
        if (!isAwsHost && !allowPrivateEgress)
        {
            return "An AWS Bedrock connection must target an AWS host, for example "
                   + "https://bedrock-runtime.eu-central-1.amazonaws.com.";
        }

        if (isAwsHost && BedrockEndpointResolution.RegionFromHost(uri.Host) is null)
        {
            return "The endpoint must name its region, for example https://bedrock-runtime.eu-central-1.amazonaws.com.";
        }

        if (target.AuthMode != AiAuthMode.SigV4 && target.AuthMode != AiAuthMode.ApiKey)
        {
            return "AWS Bedrock signs its requests with an access key; choose the API key or SigV4 authentication mode.";
        }

        // The ambient AWS credential chain is deliberately not a fallback here: in a multi-tenant deployment it
        // is the operator's identity, not the tenant's, so a profile without its own key is refused rather than
        // quietly served by someone else's role.
        return target.HasApiKey
            ? null
            : "An AWS access key is required. Store it as 'accessKeyId:secretAccessKey'.";
    }

    /// <summary>
    ///     Validates a Google target on either of its two surfaces. Which surface it is, is decided by the host,
    ///     and on Vertex the location is part of that host — so a project pinned to a region is visible in the
    ///     URL rather than hidden in a setting.
    /// </summary>
    /// <param name="target">The probe target.</param>
    /// <param name="allowPrivateEgress">When true, a host outside Google's own is permitted.</param>
    /// <param name="allowInsecureScheme">When true (Development only), a plain-http baseUrl is permitted.</param>
    public static string? ForGoogle(AiProbeTarget target, bool allowPrivateEgress, bool allowInsecureScheme)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
        {
            return "baseUrl must be an absolute URL.";
        }

        var egressError = ValidateEgress(uri, allowPrivateEgress, allowInsecureScheme);
        if (egressError is not null)
        {
            return egressError;
        }

        if (GoogleEndpointResolution.IsVertex(target.BaseUrl))
        {
            if (GoogleEndpointResolution.LocationFromHost(uri.Host) is null)
            {
                return "A Vertex AI endpoint must name its location, for example "
                       + "https://europe-west4-aiplatform.googleapis.com.";
            }

            // A service-account key is a JSON document, not a key string, and the surface will not take one
            // without the other.
            return target.HasApiKey
                ? null
                : "Vertex AI requires the JSON key of a service account that may call the Vertex AI API.";
        }

        if (!uri.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) && !allowPrivateEgress)
        {
            return "A Google connection must target a Google host, for example "
                   + "https://generativelanguage.googleapis.com.";
        }

        return target.HasApiKey ? null : "An API key is required for the Gemini API.";
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
