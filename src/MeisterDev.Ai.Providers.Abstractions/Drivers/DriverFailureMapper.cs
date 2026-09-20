// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
/// <remarks>
///     What it recognises is limited to the runtime's own types — an HTTP status, a transport failure, a timeout.
///     A vendor SDK's own exception type is recognised by the driver that carries that SDK, because this
///     assembly is the contract every driver compiles against and it takes no vendor dependency. A driver that
///     recognises its SDK's signals first and calls back here for the rest is the shape
///     <see cref="IAiProviderDriver.ClassifyRuntimeFailure" /> documents, and
///     <see cref="ClassifyStatus" /> and <see cref="ReadStatedDelay" /> are here so a driver can reuse the
///     status mapping and the stated-wait parsing once it has read its SDK's status off its own exception.
/// </remarks>
public static partial class DriverFailureMapper
{
    /// <summary>How much of a failure message is scanned for a stated wait.</summary>
    private const int MaxScannedMessageLength = 4096;

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

    /// <summary>Describes a verification that threw rather than answering.</summary>
    /// <remarks>
    ///     The chain is unwound and read the way <see cref="ClassifyRuntimeFailure" /> reads it, so the same
    ///     exception is described the same way whether it came out of a verification or out of a review. A status
    ///     the provider did answer with decides the category, because a rejected credential arriving as an
    ///     exception is still a credential problem and reporting it as an unreachable endpoint sends an operator
    ///     to the network. A timeout, a socket failure and an interrupted stream reach the endpoint category for
    ///     the same reason a refused connection does: none of them got an answer.
    /// </remarks>
    /// <param name="exception">The exception the call threw.</param>
    public static ProviderVerificationResult Failed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var category = CategoryOf(exception);

        return new ProviderVerificationResult(
            AiVerificationStatus.Failed,
            category,
            exception.Message,
            category == AiVerificationFailureCategory.EndpointReachability
                ? "Confirm the base URL, any required path prefix, and outbound connectivity."
                : ActionHintFor(StatusOf(exception)) ?? "Review the provider-specific details and try verification again.",
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

        // A status anywhere in the chain is read first. The transport cases below answer without one, and an
        // SDK that wraps a status-bearing failure in a status-less HttpRequestException would otherwise have its
        // 429 classified as an unreachable endpoint: transient either way, but not throttled, so the connection
        // is never paced and every other call on it keeps going into the same refusal.
        foreach (var candidate in Unwind(exception))
        {
            if (candidate is HttpRequestException { StatusCode: { } carried } bearing)
            {
                return ClassifyStatus((int)carried, ReadStatedDelay(bearing.Message));
            }
        }

        foreach (var candidate in Unwind(exception))
        {
            switch (candidate)
            {
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
            HttpStatusCode.TooManyRequests => "Reduce review concurrency or raise the provider-side rate limit.",
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

    /// <summary>
    ///     Classifies a failure the provider answered with an HTTP status. Public so a driver that reads a status
    ///     off its own SDK's exception maps it the same way this type maps one read off an
    ///     <see cref="HttpRequestException" />, rather than restating the status rules per family.
    /// </summary>
    /// <param name="status">The status the provider returned.</param>
    /// <param name="retryAfter">How long the provider asked the caller to wait, when it said so.</param>
    public static ProviderFailureVerdict ClassifyStatus(int status, TimeSpan? retryAfter = null)
    {
        return status switch
        {
            429 => ProviderFailureVerdict.Throttled($"The provider throttled the request (HTTP {status}).", retryAfter, status),
            408 => ProviderFailureVerdict.Transient($"The provider timed out the request (HTTP {status}).", retryAfter, status),

            // Named apart from the timeout it was folded into. The provider refused to process the request this
            // early, which is a different thing to act on and says nothing a timeout does not already say.
            425 => ProviderFailureVerdict.Transient($"The provider refused the request as premature (HTTP {status}).", retryAfter, status),
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

    /// <summary>
    ///     Reads a wait the provider stated in prose rather than in a header. OpenAI and the gateways that copy
    ///     its shape answer a throttled call with "Please try again in 4.023s" and frequently send no
    ///     <c>Retry-After</c> at all, so ignoring the body means guessing at a number the provider already gave.
    ///     Anything that does not parse yields nothing, which leaves the exponential schedule in charge.
    /// </summary>
    /// <remarks>
    ///     Public because the message an SDK exception carries is where several providers put this, and reading
    ///     it belongs with whichever driver holds that SDK.
    /// </remarks>
    /// <param name="message">The failure message, which for these SDKs carries the response body.</param>
    public static TimeSpan? ReadStatedDelay(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        // A provider that echoes the request back can make the body arbitrarily long, and the stated wait is part
        // of the error prose at the front of it, so only the opening is scanned.
        var scanned = message.Length > MaxScannedMessageLength ? message[..MaxScannedMessageLength] : message;

        Match match;
        try
        {
            match = StatedDelayPattern().Match(scanned);
        }
        catch (RegexMatchTimeoutException)
        {
            // Classification runs from inside a catch block. A throw here would replace the provider failure the
            // caller is trying to report with a failure to read it, so an unreadable body states nothing.
            return null;
        }

        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var unit = match.Groups[2].Value;
        if (unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase)
            || unit.StartsWith("milli", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMilliseconds(amount);
        }

        return unit.StartsWith("m", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(amount)
            : TimeSpan.FromSeconds(amount);
    }

    // The digits are bounded so a malformed or hostile body cannot produce a duration the runtime cannot hold;
    // whatever survives that is capped again by the retry policy before anyone waits on it.
    [GeneratedRegex(
        @"(?:try again|retry)\s+(?:in|after)\s+(\d{1,6}(?:\.\d{1,3})?)\s*(ms|milliseconds?|s|seconds?|m|minutes?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        200)]
    private static partial Regex StatedDelayPattern();

    /// <summary>
    ///     Walks a failure and everything nested inside it, flattening an <see cref="AggregateException" /> so a
    ///     caller sees each of its inner failures. Public because a driver looking for its own SDK's exception has
    ///     to look in the same places this type does; a driver that inspected the outermost exception alone would
    ///     miss a provider failure wrapped by a retry or a task boundary.
    /// </summary>
    /// <param name="exception">The failure to walk.</param>
    public static IEnumerable<Exception> Unwind(Exception exception)
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

    // The status the provider answered with, read off the first exception in the chain that carries one.
    private static int? StatusOf(Exception exception)
    {
        foreach (var candidate in Unwind(exception))
        {
            if (candidate is HttpRequestException { StatusCode: { } statusCode })
            {
                return (int)statusCode;
            }
        }

        return null;
    }

    // The category the chain describes. A status decides it; failing that, any transport failure is an endpoint
    // that did not answer, and anything else is unknown.
    private static AiVerificationFailureCategory CategoryOf(Exception exception)
    {
        // The whole chain is read for a status before any transport failure is considered, which is the
        // precedence the remark states and the one StatusOf already follows. Returning on the first transport
        // exception read an AggregateException that carries a socket failure ahead of the response explaining
        // it as an unreachable endpoint, so a 401 or a 429 was answered with network advice.
        if (StatusOf(exception) is { } status)
        {
            return MapFailureCategory((HttpStatusCode)status);
        }

        foreach (var candidate in Unwind(exception))
        {
            if (candidate is HttpRequestException or TimeoutException or SocketException or IOException)
            {
                return AiVerificationFailureCategory.EndpointReachability;
            }
        }

        return AiVerificationFailureCategory.Unknown;
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
