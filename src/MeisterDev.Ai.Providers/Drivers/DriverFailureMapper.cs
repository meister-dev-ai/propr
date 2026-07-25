// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Maps provider exceptions and HTTP results into normalized diagnostics: a verification result on the
///     configuration path, and a retry verdict on the runtime path. Both paths answer the same question about the
///     same failures, so they share one place to answer it — a provider whose 429 is understood at probe time but
///     not at runtime would retry inconsistently for no reason a reader could discover.
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
            ActionHintFor(statusCode),
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

    /// <summary>
    ///     Classifies a runtime provider failure for retry purposes, without reference to any one SDK's exception
    ///     hierarchy: the HTTP status, or the absence of a response at all, is what decides. A driver whose SDK
    ///     signals throttling some other way overrides
    ///     <see cref="IAiProviderDriver.ClassifyRuntimeFailure" /> and calls back here for the rest.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    public static ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var candidate in Unwind(exception))
        {
            switch (candidate)
            {
                case ClientResultException clientResult:
                    return FromStatus(clientResult.Status, ReadRetryAfter(clientResult));
                case HttpRequestException { StatusCode: { } statusCode }:
                    return FromStatus((int)statusCode, null);
                case HttpRequestException httpRequest:
                    return ProviderFailureVerdict.Transient($"The provider endpoint could not be reached ({httpRequest.HttpRequestError}).");
                case TimeoutException:
                    return ProviderFailureVerdict.Transient("The provider did not respond before the request timed out.");
                case SocketException socket:
                    return ProviderFailureVerdict.Transient($"The connection to the provider failed ({socket.SocketErrorCode}).");
                case IOException:
                    return ProviderFailureVerdict.Transient("The connection to the provider was interrupted.");
                default:
                    continue;
            }
        }

        // Nothing in the chain looks like a transport or protocol failure, so repeating the call would only
        // repeat the same defect. Saying so is more useful than an optimistic retry that burns the budget.
        return ProviderFailureVerdict.Permanent(exception.Message);
    }

    /// <summary>
    ///     What an operator should try next for a given HTTP status. Shared by the verification path and the
    ///     runtime failure message so the same status never comes with two different pieces of advice.
    /// </summary>
    /// <param name="statusCode">The status the provider returned.</param>
    public static string ActionHintFor(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "Check the configured API key or credential source.",
            HttpStatusCode.Forbidden => "Confirm the credential has permission to access the requested models.",
            HttpStatusCode.NotFound => "Confirm the base URL is correct, including any required path prefix.",
            HttpStatusCode.TooManyRequests =>
                "Reduce review concurrency or raise the provider-side rate limit; the call was already retried with backoff.",
            HttpStatusCode.RequestTimeout => "Retry later, or check the network path to the provider.",
            _ when (int)statusCode >= 500 => "Retry later or inspect provider-side service health.",
            _ => "Inspect the provider response and update the profile settings before retrying.",
        };
    }

    /// <summary>
    ///     What an operator should try next for a failure that may not have carried a status at all.
    /// </summary>
    /// <param name="httpStatus">The status behind the failure, or <see langword="null" /> when there was none.</param>
    public static string? ActionHintFor(int? httpStatus)
    {
        return httpStatus is { } status ? ActionHintFor((HttpStatusCode)status) : null;
    }

    private static ProviderFailureVerdict FromStatus(int status, TimeSpan? retryAfter)
    {
        return status switch
        {
            429 => ProviderFailureVerdict.Transient($"The provider throttled the request (HTTP {status}).", retryAfter, status),
            408 or 425 => ProviderFailureVerdict.Transient($"The provider timed out the request (HTTP {status}).", retryAfter, status),
            // 501 and 505 are server-side statuses that repeating cannot change: the provider does not implement
            // what was asked, and it will not start to on the second attempt.
            501 or 505 => ProviderFailureVerdict.Permanent($"The provider does not support this request (HTTP {status}).", status),
            >= 500 => ProviderFailureVerdict.Transient($"The provider returned a server error (HTTP {status}).", retryAfter, status),
            401 => ProviderFailureVerdict.Permanent($"The provider rejected the credential (HTTP {status}).", status),
            403 => ProviderFailureVerdict.Permanent($"The credential is not permitted to make this call (HTTP {status}).", status),
            404 => ProviderFailureVerdict.Permanent($"The provider has no such endpoint or model (HTTP {status}).", status),
            _ => ProviderFailureVerdict.Permanent($"The provider rejected the request (HTTP {status}).", status),
        };
    }

    // Providers state their own backoff on 429 and sometimes on 503. Honouring it beats guessing, so it is read
    // off the raw response rather than left to the exponential schedule.
    private static TimeSpan? ReadRetryAfter(ClientResultException exception)
    {
        var response = exception.GetRawResponse();
        if (response is null || !response.Headers.TryGetValue("Retry-After", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // The header also allows an HTTP date. A date already in the past yields no wait rather than a negative one.
        return DateTimeOffset.TryParse(value, out var until)
            ? Max(until - DateTimeOffset.UtcNow, TimeSpan.Zero)
            : null;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left > right ? left : right;
    }

    private static IEnumerable<Exception> Unwind(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var nested in Unwind(inner))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            yield return candidate;
        }
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
}
