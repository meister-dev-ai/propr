// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Networking;

/// <summary>
///     Identifies Microsoft-controlled Azure AI hostnames. Azure OpenAI / Azure AI Foundry endpoints — including
///     private endpoints — always use these hostnames, so restricting an admin-supplied Azure <c>baseUrl</c> to
///     them stops the Azure SDK (which has no connect-time egress guard) from being pointed at an internal host.
/// </summary>
public static class AzureAiHostPolicy
{
    private static readonly string[] AzureAiHostSuffixes =
    [
        ".openai.azure.com",
        ".services.ai.azure.com",
        ".cognitiveservices.azure.com",
    ];

    /// <summary>Returns <c>true</c> when <paramref name="host" /> is an Azure AI host.</summary>
    /// <param name="host">The URL host component.</param>
    public static bool IsAzureAiHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.TrimEnd('.');
        foreach (var suffix in AzureAiHostSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
