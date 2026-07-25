// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.Net;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Maps provider exceptions and HTTP results into normalized verification diagnostics.
/// </summary>
public static class DriverFailureMapper
{
    public static ProviderVerificationResult Verified(string summary, IReadOnlyList<string>? warnings = null)
    {
        return new ProviderVerificationResult(
            AiVerificationStatus.Verified,
            null,
            summary,
            null,
            DateTimeOffset.UtcNow,
            warnings ?? []);
    }

    public static ProviderVerificationResult Failed(HttpStatusCode statusCode, string? detail = null)
    {
        return new ProviderVerificationResult(
            AiVerificationStatus.Failed,
            MapFailureCategory(statusCode),
            detail ?? $"Provider request failed with status {(int)statusCode}.",
            BuildActionHint(statusCode),
            DateTimeOffset.UtcNow,
            [],
            new Dictionary<string, string>
            {
                ["httpStatus"] = ((int)statusCode).ToString(),
            });
    }

    public static ProviderVerificationResult Failed(ClientResultException exception)
    {
        return Failed((HttpStatusCode)exception.Status, exception.Message);
    }

    public static ProviderVerificationResult Failed(Exception exception)
    {
        var category = exception is HttpRequestException
            ? AiVerificationFailureCategory.EndpointReachability
            : AiVerificationFailureCategory.Unknown;

        return new ProviderVerificationResult(
            AiVerificationStatus.Failed,
            category,
            exception.Message,
            category == AiVerificationFailureCategory.EndpointReachability
                ? "Confirm the base URL, any required path prefix, and outbound connectivity."
                : "Review the provider-specific details and try verification again.",
            DateTimeOffset.UtcNow,
            [],
            new Dictionary<string, string>
            {
                ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name,
            });
    }

    private static AiVerificationFailureCategory MapFailureCategory(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => AiVerificationFailureCategory.Credentials,
            HttpStatusCode.Forbidden => AiVerificationFailureCategory.Authorization,
            HttpStatusCode.NotFound => AiVerificationFailureCategory.EndpointReachability,
            HttpStatusCode.BadRequest => AiVerificationFailureCategory.ProviderRejected,
            _ when (int)statusCode >= 500 => AiVerificationFailureCategory.ProviderRejected,
            _ => AiVerificationFailureCategory.Unknown,
        };
    }

    private static string BuildActionHint(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "Check the configured API key or credential source.",
            HttpStatusCode.Forbidden => "Confirm the credential has permission to access the requested models.",
            HttpStatusCode.NotFound => "Confirm the base URL is correct, including any required path prefix.",
            _ when (int)statusCode >= 500 => "Retry later or inspect provider-side service health.",
            _ => "Inspect the provider response and update the profile settings before retrying.",
        };
    }
}
