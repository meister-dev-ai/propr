// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.Net;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AI.Providers;

/// <summary>
///     Maps provider exceptions and HTTP results into normalized verification diagnostics.
/// </summary>
public static class DriverFailureMapper
{
    public static AiVerificationResultDto Verified(string summary, IReadOnlyList<string>? warnings = null)
    {
        return new AiVerificationResultDto(
            AiVerificationStatus.Verified,
            null,
            summary,
            null,
            DateTimeOffset.UtcNow,
            warnings ?? []);
    }

    public static AiVerificationResultDto Failed(HttpStatusCode statusCode, string? detail = null)
    {
        return new AiVerificationResultDto(
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

    public static AiVerificationResultDto Failed(ClientResultException exception)
    {
        return Failed((HttpStatusCode)exception.Status, exception.Message);
    }

    public static AiVerificationResultDto Failed(Exception exception)
    {
        var category = exception is HttpRequestException
            ? AiVerificationFailureCategory.EndpointReachability
            : AiVerificationFailureCategory.Unknown;

        return new AiVerificationResultDto(
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
