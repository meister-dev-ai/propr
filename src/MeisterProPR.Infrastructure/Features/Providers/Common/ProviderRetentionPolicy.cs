// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

/// <summary>Shared bounded-retention and failure-categorization rules for provider operational data.</summary>
public static class ProviderRetentionPolicy
{
    public static readonly TimeSpan WebhookDeliveryRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan ProviderConnectionAuditRetention = TimeSpan.FromDays(180);

    public static DateTimeOffset GetWebhookDeliveryCutoff(DateTimeOffset now)
    {
        return now - WebhookDeliveryRetention;
    }

    public static DateTimeOffset GetProviderConnectionAuditCutoff(DateTimeOffset now)
    {
        return now - ProviderConnectionAuditRetention;
    }

    public static string? CategorizeFailure(string? message, string? workflowStage = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.IsNullOrWhiteSpace(workflowStage)
                ? null
                : NormalizeStageFallback(workflowStage);
        }

        var normalizedMessage = message.Trim().ToLowerInvariant();
        var stageFallback = NormalizeStageFallback(workflowStage);

        if (IsWebhookTrustFailure(normalizedMessage))
        {
            return "webhookTrust";
        }

        if (IsAuthenticationFailure(normalizedMessage))
        {
            return "authentication";
        }

        if (IsPublicationFailure(normalizedMessage))
        {
            return "publication";
        }

        if (IsReviewRetrievalFailure(normalizedMessage))
        {
            return "reviewRetrieval";
        }

        if (IsDiscoveryFailure(normalizedMessage))
        {
            return "discovery";
        }

        if (IsConfigurationFailure(normalizedMessage))
        {
            return "configuration";
        }

        return stageFallback ?? "unknown";
    }

    private static bool IsWebhookTrustFailure(string normalizedMessage)
    {
        return normalizedMessage.Contains("signature", StringComparison.Ordinal)
               || normalizedMessage.Contains("authorization header", StringComparison.Ordinal)
               || normalizedMessage.Contains("secret", StringComparison.Ordinal)
               || (normalizedMessage.Contains("webhook", StringComparison.Ordinal) &&
                   normalizedMessage.Contains("invalid", StringComparison.Ordinal));
    }

    private static bool IsAuthenticationFailure(string normalizedMessage)
    {
        return normalizedMessage.Contains("token", StringComparison.Ordinal)
               || normalizedMessage.Contains("credential", StringComparison.Ordinal)
               || normalizedMessage.Contains("unauthorized", StringComparison.Ordinal)
               || normalizedMessage.Contains("forbidden", StringComparison.Ordinal)
               || normalizedMessage.Contains("permission", StringComparison.Ordinal)
               || normalizedMessage.Contains("scope", StringComparison.Ordinal)
               || normalizedMessage.Contains("auth", StringComparison.Ordinal);
    }

    private static bool IsPublicationFailure(string normalizedMessage)
    {
        return normalizedMessage.Contains("publish", StringComparison.Ordinal)
               || normalizedMessage.Contains("comment", StringComparison.Ordinal)
               || normalizedMessage.Contains("thread", StringComparison.Ordinal)
               || normalizedMessage.Contains("reply", StringComparison.Ordinal);
    }

    private static bool IsReviewRetrievalFailure(string normalizedMessage)
    {
        return normalizedMessage.Contains("review", StringComparison.Ordinal)
               || normalizedMessage.Contains("pull request", StringComparison.Ordinal)
               || normalizedMessage.Contains("merge request", StringComparison.Ordinal)
               || normalizedMessage.Contains("revision", StringComparison.Ordinal);
    }

    private static bool IsDiscoveryFailure(string normalizedMessage)
    {
        return normalizedMessage.Contains("discover", StringComparison.Ordinal)
               || normalizedMessage.Contains("repository", StringComparison.Ordinal)
               || normalizedMessage.Contains("group", StringComparison.Ordinal)
               || normalizedMessage.Contains("organization", StringComparison.Ordinal)
               || normalizedMessage.Contains("namespace", StringComparison.Ordinal);
    }

    private static bool IsConfigurationFailure(string normalizedMessage)
    {
        return normalizedMessage.Contains("host", StringComparison.Ordinal)
               || normalizedMessage.Contains("url", StringComparison.Ordinal)
               || normalizedMessage.Contains("timeout", StringComparison.Ordinal)
               || normalizedMessage.Contains("dns", StringComparison.Ordinal)
               || normalizedMessage.Contains("connect", StringComparison.Ordinal)
               || normalizedMessage.Contains("malformed", StringComparison.Ordinal);
    }

    public static string NormalizeStatus(string status, string fallback)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return fallback;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "info" => "info",
            "success" => "success",
            "warning" => "warning",
            "error" => "error",
            _ => fallback,
        };
    }

    private static string? NormalizeStageFallback(string? workflowStage)
    {
        if (string.IsNullOrWhiteSpace(workflowStage))
        {
            return null;
        }

        return workflowStage.Trim().ToLowerInvariant() switch
        {
            "webhook" or "webhooktrust" => "webhookTrust",
            "discovery" or "onboarding" => "discovery",
            "publication" or "publishing" or "mentionreply" => "publication",
            "review" or "reviewretrieval" or "execution" => "reviewRetrieval",
            "authentication" or "authorization" => "authentication",
            "configuration" => "configuration",
            _ => null,
        };
    }
}
