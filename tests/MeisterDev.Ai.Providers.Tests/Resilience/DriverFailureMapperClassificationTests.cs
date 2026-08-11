// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers the shared classification the runtime retry path acts on. The point of these is that retry is
///     decided by what the provider said, not by which SDK's exception type carried it — a provider added later
///     inherits this behaviour without the classifier being reopened.
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

    [Fact]
    public void AProviderStatedRetryAfterInSecondsIsCarriedOnTheVerdict()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429, retryAfter: "7"));

        Assert.Equal(TimeSpan.FromSeconds(7), verdict.RetryAfter);
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

    // OpenAI and the gateways that speak its shape state the wait in the error body and often send no header at
    // all, so the body is read rather than the wait being guessed at.
    [Fact]
    public void AStatedDelayInTheBodyIsReadWhenNoHeaderCarriesOne()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429, message: RateLimitBody("Please try again in 4.023s.")));

        Assert.Equal(TimeSpan.FromSeconds(4.023), verdict.RetryAfter);
    }

    [Fact]
    public void AStatedDelayInMillisecondsIsReadAsAFractionOfASecond()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429, message: RateLimitBody("Please try again in 500ms.")));

        Assert.Equal(TimeSpan.FromMilliseconds(500), verdict.RetryAfter);
    }

    [Fact]
    public void ABodyThatStatesNoDelayLeavesTheScheduleToDecide()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429, message: RateLimitBody("Rate limit reached for this organization.")));

        Assert.True(verdict.IsThrottled);
        Assert.Null(verdict.RetryAfter);
    }

    // The header is the protocol's answer and the body is the provider's prose, so the header wins where both
    // are present.
    [Fact]
    public void TheHeaderWinsOverADelayStatedInTheBody()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429, retryAfter: "7", message: RateLimitBody("Please try again in 4.023s.")));

        Assert.Equal(TimeSpan.FromSeconds(7), verdict.RetryAfter);
    }

    // A header of zero is the provider taking back the wait it just offered. Reading it as "come straight back"
    // would throw away a real number the body gave and send the fan-out into the quota it was asked to wait out.
    [Fact]
    public void ARetryAfterOfZeroDoesNotMaskADelayStatedInTheBody()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(429, retryAfter: "0", message: RateLimitBody("Please try again in 4.023s.")));

        Assert.Equal(TimeSpan.FromSeconds(4.023), verdict.RetryAfter);
    }

    // Anthropic, Google and Vertex report a 429 as an HttpRequestException rather than through the OpenAI SDK's
    // own type, and the body rides along in the message, so the stated wait has to be read from there as well.
    [Fact]
    public void AStatedDelayIsReadFromAnHttpRequestExceptionToo()
    {
        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(
            new HttpRequestException(RateLimitBody("Please try again in 12s."), null, HttpStatusCode.TooManyRequests));

        Assert.True(verdict.IsThrottled);
        Assert.Equal(TimeSpan.FromSeconds(12), verdict.RetryAfter);
    }

    [Fact]
    public void ARetryAfterDateAlreadyPastYieldsNoWaitRatherThanANegativeOne()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("R");

        var verdict = DriverFailureMapper.ClassifyRuntimeFailure(HttpFailure(503, retryAfter: past));

        Assert.Equal(TimeSpan.Zero, verdict.RetryAfter);
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

    private static ClientResultException HttpFailure(int status, string? retryAfter = null, string? message = null)
    {
        var response = new StubResponse(status, retryAfter);
        return message is null ? new ClientResultException(response) : new ClientResultException(message, response);
    }

    /// <summary>The shape a rate-limit failure arrives in: the status line, then the provider's own JSON body.</summary>
    private static string RateLimitBody(string detail)
    {
        return "HTTP 429 (Too Many Requests)\n\n"
               + "{\"error\":{\"message\":\"Rate limit reached for gpt-4o. " + detail + "\",\"type\":\"tokens\"}}";
    }

    /// <summary>Minimal transport response so a real <see cref="ClientResultException" /> can be constructed.</summary>
    private sealed class StubResponse(int status, string? retryAfter) : PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString(string.Empty);

        protected override PipelineResponseHeaders HeadersCore { get; } = new StubHeaders(retryAfter);

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => this.Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(this.Content);

        public override void Dispose()
        {
        }
    }

    private sealed class StubHeaders(string? retryAfter) : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            if (retryAfter is not null)
            {
                yield return new KeyValuePair<string, string>("Retry-After", retryAfter);
            }
        }

        public override bool TryGetValue(string name, out string? value)
        {
            var matches = retryAfter is not null && string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase);
            value = matches ? retryAfter : null;
            return matches;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            var found = this.TryGetValue(name, out var value);
            values = found ? [value!] : null;
            return found;
        }
    }
}
