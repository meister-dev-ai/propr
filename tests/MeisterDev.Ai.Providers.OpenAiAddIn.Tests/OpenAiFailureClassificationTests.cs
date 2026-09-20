// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.OpenAiAddIn.Tests;

/// <summary>
///     How this family classifies a refusal the OpenAI client library raised, which the retry stage acts
///     on.
/// </summary>
/// <remarks>
///     The library's exception type is named by the assembly that carries the library, not by the contract every
///     family compiles against, so this family recognises it in its own classifier. A family that lost that
///     recognition would stop retrying a throttled call and would report the same failure as a permanent one,
///     which is a spend and a latency change with nothing in the log to point at it.
/// </remarks>
public sealed class OpenAiFailureClassificationTests
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
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(status));

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
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(status));

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
        Assert.False(Driver().ClassifyRuntimeFailure(SdkFailure(status)).IsTransient);
    }

    [Fact]
    public void AProviderStatedRetryAfterInSecondsIsCarriedOnTheVerdict()
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, retryAfter: "7"));

        Assert.Equal(TimeSpan.FromSeconds(7), verdict.RetryAfter);
    }

    // Throttling is separated from the other transient classes so a later stage can pace the connection without
    // re-reading the HTTP status off an exception it was never given.
    [Fact]
    public void AThrottleIsMarkedAsOneRatherThanOnlyAsTransient()
    {
        var throttled = Driver().ClassifyRuntimeFailure(SdkFailure(429));
        var serverError = Driver().ClassifyRuntimeFailure(SdkFailure(503));

        Assert.True(throttled.IsThrottled);
        Assert.True(throttled.IsTransient);
        Assert.False(serverError.IsThrottled);
    }

    // This vendor states the wait in the error body and often sends no header at all, so the body is read rather
    // than the wait being guessed at.
    [Fact]
    public void AStatedDelayInTheBodyIsReadWhenNoHeaderCarriesOne()
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, message: RateLimitBody("Please try again in 4.023s.")));

        Assert.Equal(TimeSpan.FromSeconds(4.023), verdict.RetryAfter);
    }

    [Fact]
    public void AStatedDelayInMillisecondsIsReadAsAFractionOfASecond()
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, message: RateLimitBody("Please try again in 500ms.")));

        Assert.Equal(TimeSpan.FromMilliseconds(500), verdict.RetryAfter);
    }

    [Fact]
    public void ABodyThatStatesNoDelayLeavesTheScheduleToDecide()
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, message: RateLimitBody("Rate limit reached for this organization.")));

        Assert.True(verdict.IsThrottled);
        Assert.Null(verdict.RetryAfter);
    }

    // The header is the protocol's answer and the body is the provider's prose, so the header wins where both
    // are present.
    [Fact]
    public void TheHeaderWinsOverADelayStatedInTheBody()
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, retryAfter: "7", message: RateLimitBody("Please try again in 4.023s.")));

        Assert.Equal(TimeSpan.FromSeconds(7), verdict.RetryAfter);
    }

    // A header of zero is the provider taking back the wait it just offered. Reading it as "come straight back"
    // would throw away a real number the body gave and send the fan-out into the quota it was asked to wait out.
    [Fact]
    public void ARetryAfterOfZeroDoesNotMaskADelayStatedInTheBody()
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, retryAfter: "0", message: RateLimitBody("Please try again in 4.023s.")));

        Assert.Equal(TimeSpan.FromSeconds(4.023), verdict.RetryAfter);
    }

    // The header is an unvalidated string the provider chose. A value naming no time at all has to leave the
    // schedule in charge; read as some number it would pace the whole fan-out against a value nobody sent. A
    // negative count is not a wait either, and neither is an absent value sent as an empty header.
    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("   ")]
    public void ARetryAfterThatNamesNoWaitLeavesTheScheduleToDecide(string retryAfter)
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, retryAfter: retryAfter));

        Assert.True(verdict.IsThrottled);
        Assert.Null(verdict.RetryAfter);
    }

    // A header that cannot be read is not a reason to ignore a wait the provider stated in its body.
    [Fact]
    public void AMalformedRetryAfterDoesNotMaskADelayStatedInTheBody()
    {
        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(429, retryAfter: "soon", message: RateLimitBody("Please try again in 4.023s.")));

        Assert.Equal(TimeSpan.FromSeconds(4.023), verdict.RetryAfter);
    }

    [Fact]
    public void ARetryAfterDateAlreadyPastYieldsNoWaitRatherThanANegativeOne()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("R");

        var verdict = Driver().ClassifyRuntimeFailure(SdkFailure(503, retryAfter: past));

        Assert.Equal(TimeSpan.Zero, verdict.RetryAfter);
    }

    // A library failure buried under a wrapper still classifies, because the library reports one that way
    // whenever the call went through a task boundary.
    [Fact]
    public void AWrappedSdkFailureIsStillFound()
    {
        var wrapped = new InvalidOperationException("the client failed", SdkFailure(429));

        Assert.True(Driver().ClassifyRuntimeFailure(wrapped).IsThrottled);
    }

    // A failure the library did not raise falls through to the shared rule rather than being classified here.
    [Fact]
    public void AFailureTheSdkDidNotRaiseIsLeftToTheSharedRule()
    {
        var verdict = Driver().ClassifyRuntimeFailure(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        Assert.True(verdict.IsTransient);
        Assert.Equal(
            DriverFailureMapper
                .ClassifyRuntimeFailure(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"))
                .Reason,
            verdict.Reason);
    }

    private static OpenAiProviderDriver Driver()
    {
        return new OpenAiProviderDriver();
    }

    private static ClientResultException SdkFailure(int status, string? retryAfter = null, string? message = null)
    {
        var response = new StubResponse(status, retryAfter);
        return message is null ? new ClientResultException(response) : new ClientResultException(message, response);
    }

    /// <summary>The shape a rate-limit failure arrives in: the status line, then the provider's own JSON body.</summary>
    private static string RateLimitBody(string detail)
    {
        return "HTTP 429 (Too Many Requests)\n\n"
               + "{\"error\":{\"message\":\"Rate limit reached for gpt-5. " + detail + "\",\"type\":\"tokens\"}}";
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
