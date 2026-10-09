// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Features.Providers.Support;

/// <summary>Preserves public SCM names and compatibility parser contexts.</summary>
public static class ScmProviderVocabulary
{
    public const ScmProvider AzureDevOpsCompatibilityDefault = ScmProvider.AzureDevOps;
    public const WebhookProviderType AzureDevOpsCompatibilityWebhookDefault = WebhookProviderType.AzureDevOps;

    private static readonly IReadOnlyDictionary<ScmProvider, string> Names = Enum.GetValues<ScmProvider>().ToDictionary(
        provider => provider, provider => typeof(ScmProvider).GetField(provider.ToString())!
            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? provider.ToString());

    public static bool TryParseManagement(string? value, out ScmProvider? provider)
    {
        provider = null;
        if (value is null)
        {
            return true;
        }

        foreach (var entry in Names)
        {
            if (string.Equals(value, entry.Value, StringComparison.Ordinal))
            {
                provider = entry.Key;
                return true;
            }
        }

        return false;
    }

    public static string ToPublicName(ScmProvider provider) => Names.TryGetValue(provider, out var name)
        ? name
        : throw new InvalidOperationException("The review target provider is unavailable.");

    /// <summary>Decodes recorded canonical-reference names using their existing case-insensitive compatibility grammar.</summary>
    public static bool TryParseRecordedCanonicalProvider(string? value, out ScmProvider provider)
    {
        foreach (var entry in Names)
        {
            if (string.Equals(value, entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                provider = entry.Key;
                return true;
            }
        }

        provider = default;
        return false;
    }

    public static bool TryParseWebhook(string value, out ScmProvider provider)
    {
        if (string.Equals(value, "ado", StringComparison.OrdinalIgnoreCase))
        {
            provider = AzureDevOpsCompatibilityDefault;
            return true;
        }

        return Enum.TryParse(value, true, out provider);
    }

    public static ScmProvider FromWebhook(WebhookProviderType provider) => TryFromWebhook(provider, out var mapped)
        ? mapped
        : throw new ArgumentOutOfRangeException(nameof(provider), provider, null);

    public static WebhookProviderType ToWebhook(ScmProvider provider) =>
        Enum.GetValues<WebhookProviderType>().Single(value => TryFromWebhook(value, out var mapped) && mapped == provider);

    public static bool TryFromWebhook(WebhookProviderType provider, out ScmProvider mapped)
    {
        ScmProvider? candidate = provider switch
        {
            WebhookProviderType.AzureDevOps => ScmProvider.AzureDevOps,
            WebhookProviderType.GitHub => ScmProvider.GitHub,
            WebhookProviderType.GitLab => ScmProvider.GitLab,
            WebhookProviderType.Forgejo => ScmProvider.Forgejo,
            _ => null,
        };
        mapped = candidate.GetValueOrDefault();
        return candidate.HasValue;
    }

    public static string ToWebhookSegment(WebhookProviderType provider) => provider == WebhookProviderType.AzureDevOps
        ? "ado"
        : provider.ToString().ToLowerInvariant();

    public static string ToTelemetryTag(ScmProvider provider) => provider.ToString().ToLowerInvariant();
}
