// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers the shared classification the runtime retry path acts on. The point of these is that retry is
///     decided by what the provider said, not by which SDK's exception type carried it — a provider added later
///     inherits this behaviour without the classifier being reopened. What a vendor SDK's own exception type
///     classifies as is covered beside the driver that carries that SDK.
/// </summary>
public sealed class DriverFailureMapperClassificationTests
{
    [Theory]
    [InlineData(429)]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void StatusesThatCanClearOnTheirOwnAreTransient(int status)
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(status));

        Assert.True(verdict.IsTransient);
        Assert.Equal(status, verdict.HttpStatus);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void StatusesThatDescribeTheRequestOrCredentialArePermanent(int status)
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(status));

        Assert.False(verdict.IsTransient);
        Assert.Equal(status, verdict.HttpStatus);
    }

    // 5xx is not uniformly worth repeating: these two say the provider will not do what was asked, and a second
    // identical request gets the same answer. Retrying them would only spend the budget twice.
    [Theory]
    [InlineData(501)]
    [InlineData(505)]
    public void ServerStatusesThatDenyTheRequestItselfArePermanent(int status)
    {
        Assert.False(DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(status)).IsTransient);
    }

    // Throttling is separated from the other transient classes so a later stage can pace the connection without
    // re-reading the HTTP status off an exception it was never given.
    [Fact]
    public void AThrottleIsMarkedAsOneRatherThanOnlyAsTransient()
    {
        var throttled = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429));
        var serverError = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(503));

        Assert.True(throttled.IsThrottled);
        Assert.True(throttled.IsTransient);
        Assert.False(serverError.IsThrottled);
    }

    // Anthropic, Google and Vertex report a 429 as an HttpRequestException rather than through a vendor SDK's own
    // type, and the body rides along in the message, so the stated wait has to be read from there as well.
    [Fact]
    public void AStatedDelayIsReadFromAnHttpRequestExceptionToo()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(
            new HttpRequestException(RateLimitBody("Please try again in 12s."), null, HttpStatusCode.TooManyRequests));

        Assert.True(verdict.IsThrottled);
        Assert.Equal(TimeSpan.FromSeconds(12), verdict.RetryAfter);
    }

    [Fact]
    public void AStatedDelayInMillisecondsIsReadAsAFractionOfASecond()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(
            new HttpRequestException(RateLimitBody("Please try again in 500ms."), null, HttpStatusCode.TooManyRequests));

        Assert.Equal(TimeSpan.FromMilliseconds(500), verdict.RetryAfter);
    }

    [Fact]
    public void ABodyThatStatesNoDelayLeavesTheScheduleToDecide()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(
            new HttpRequestException(RateLimitBody("Rate limit reached for this organization."), null, HttpStatusCode.TooManyRequests));

        Assert.True(verdict.IsThrottled);
        Assert.Null(verdict.RetryAfter);
    }

    [Fact]
    public void AnUnreachableEndpointIsTransientEvenWithNoStatusToReadIt()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        Assert.True(verdict.IsTransient);
        Assert.Null(verdict.HttpStatus);
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    public void TransportFailuresWithNoResponseAtAllAreTransient(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(DriverFailureMapper.ClassifyRuntimeFailure(exception).IsTransient);
    }

    [Fact]
    public void ASocketFailureIsTransientAndNamesTheSocketError()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(new SocketException((int)SocketError.HostUnreachable));

        Assert.True(verdict.IsTransient);
        Assert.Contains("HostUnreachable", verdict.Reason, StringComparison.Ordinal);
    }

    // The classifier looks through wrappers, because an SDK reporting a network failure usually buries the
    // HttpRequestException one or two levels down rather than throwing it directly.
    [Fact]
    public void AWrappedTransportFailureIsStillFound()
    {
        var wrapped = new InvalidOperationException(
            "the client failed",
            new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        Assert.True(DriverFailureMapper.ClassifyRuntimeFailure(wrapped).IsTransient);
    }

    // An aggregate is flattened as well, because a failure raised inside a parallel pass arrives wrapped in one.
    [Fact]
    public void AFailureInsideAnAggregateIsStillFound()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        Assert.True(DriverFailureMapper.ClassifyRuntimeFailure(aggregate).IsTransient);
    }

    [Fact]
    public void AFailureThatIsNotATransportFailureAtAllIsPermanent()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(new InvalidOperationException("the model id is malformed"));

        Assert.False(verdict.IsTransient);
        Assert.Equal("the model id is malformed", verdict.Reason);
    }

    [Fact]
    public void OneStatusYieldsOneActionHintOnBothPaths()
    {
        var fromVerification = DriverFailureMapper.Failed(HttpStatusCode.Unauthorized);
        var fromRuntime = DriverFailureMapper.ActionHintFor(401);

        Assert.Equal(fromVerification.ActionHint, fromRuntime);
    }

    // A driver that reads a status off its own SDK's exception maps it through the same rules rather than
    // restating them, and that keeps one status from meaning two things across families.
    [Fact]
    public void AStatusReadByADriverClassifiesTheSameWayAsOneReadFromATransportFailure()
    {
        var fromDriver = DriverFailureMapper.ClassifyStatus(429, TimeSpan.FromSeconds(3));
        var fromTransport = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429));

        Assert.Equal(fromTransport.IsThrottled, fromDriver.IsThrottled);
        Assert.Equal(fromTransport.Reason, fromDriver.Reason);
        Assert.Equal(TimeSpan.FromSeconds(3), fromDriver.RetryAfter);
    }

    // A verification that threw is described the way the runtime path describes the same exception. It is a
    // public helper add-in authors are told to call, so a family reporting a rejected credential as an
    // unreachable endpoint sends the operator to the network instead of to the key.
    [Theory]
    [InlineData(401, AiVerificationFailureCategory.Credentials)]
    [InlineData(403, AiVerificationFailureCategory.Authorization)]
    [InlineData(404, AiVerificationFailureCategory.EndpointReachability)]
    [InlineData(400, AiVerificationFailureCategory.ProviderRejected)]
    [InlineData(503, AiVerificationFailureCategory.ProviderRejected)]
    public void AFailureCarryingAStatusIsCategorisedByThatStatus(int status, AiVerificationFailureCategory expected)
    {
        Assert.Equal(expected, DriverFailureMapper.Failed(HttpFailure(status)).FailureCategory);
    }

    // The four the runtime classifier treats as transport failures. None of them got an answer, so all four are
    // the endpoint not being reached; leaving three of them unknown gives the operator no remedy to try.
    [Fact]
    public void EveryTransportFailureIsCategorisedAsTheEndpointNotBeingReached()
    {
        Exception[] transport =
        [
            new HttpRequestException("no route"),
            new TimeoutException("timed out"),
            new SocketException((int)SocketError.ConnectionRefused),
            new IOException("stream ended"),
        ];

        Assert.All(
            transport,
            failure => Assert.Equal(
                AiVerificationFailureCategory.EndpointReachability,
                DriverFailureMapper.Failed(failure).FailureCategory));
    }

    [Fact]
    public void AStatusWrappedInsideAnotherExceptionIsStillRead()
    {
        var wrapped = new InvalidOperationException("the call failed", HttpFailure(401));

        Assert.Equal(AiVerificationFailureCategory.Credentials, DriverFailureMapper.Failed(wrapped).FailureCategory);
    }

    [Fact]
    public void AFailureThatIsNeitherAStatusNorTransportStaysUnknown()
    {
        Assert.Equal(
            AiVerificationFailureCategory.Unknown,
            DriverFailureMapper.Failed(new InvalidOperationException("the family is misconfigured")).FailureCategory);
    }

    // The verification path has retried nothing, so the hint cannot say the call was already retried with
    // backoff: an operator reading it would stop looking for a retry that never happened.
    [Fact]
    public void TheThrottlingHintDoesNotClaimARetryThatDidNotHappen()
    {
        Assert.DoesNotContain(
            "already retried",
            DriverFailureMapper.ActionHintFor(HttpStatusCode.TooManyRequests),
            StringComparison.OrdinalIgnoreCase);
    }

    // A status anywhere in the chain decides, so an SDK that wraps a refusal in a status-less transport
    // exception still has its 429 read as throttling. Transient either way; the difference is whether the
    // connection is paced or every other call on it walks into the same refusal.
    [Fact]
    public void AStatusWrappedInAStatuslessTransportFailureIsStillRead()
    {
        var wrapped = new HttpRequestException(
            "The endpoint could not be reached.",
            HttpFailure(429),
            statusCode: null);

        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(wrapped);

        Assert.True(verdict.IsThrottled);
        Assert.True(verdict.IsTransient);
    }

    [Fact]
    public void AStatuslessTransportFailureWithNothingUnderItStaysTransient()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(new HttpRequestException("The endpoint could not be reached."));

        Assert.True(verdict.IsTransient);
        Assert.False(verdict.IsThrottled);
    }

    private static HttpRequestException HttpFailure(int status)
    {
        return new HttpRequestException($"HTTP {status}", null, (HttpStatusCode)status);
    }

    /// <summary>The shape a rate-limit failure arrives in: the status line, then the provider's own JSON body.</summary>
    private static string RateLimitBody(string detail)
    {
        return "HTTP 429 (Too Many Requests)\n\n"
               + "{\"error\":{\"message\":\"Rate limit reached for gpt-4o. " + detail + "\",\"type\":\"tokens\"}}";
    }
}
