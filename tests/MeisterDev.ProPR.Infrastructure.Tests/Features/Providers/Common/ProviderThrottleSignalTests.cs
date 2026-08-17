// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class ProviderThrottleSignalTests
{
    [Fact]
    public void IsThrottled_TooManyRequests_IsThrottling()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.True(ProviderThrottleSignal.IsThrottled(response));
    }

    [Fact]
    public void IsThrottled_ForbiddenWithRetryAfter_IsThrottling()
    {
        // GitHub reports a secondary rate limit this way.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));

        Assert.True(ProviderThrottleSignal.IsThrottled(response));
    }

    [Fact]
    public void IsThrottled_ForbiddenWithExhaustedRateLimit_IsThrottling()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");

        Assert.True(ProviderThrottleSignal.IsThrottled(response));
    }

    [Fact]
    public void IsThrottled_PlainForbidden_IsNotThrottling()
    {
        // A token without the scope answers this way, and reporting it as a throttle would have an operator
        // waiting for a limit to clear that never applied.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        Assert.False(ProviderThrottleSignal.IsThrottled(response));
    }

    [Fact]
    public void IsThrottled_ServiceUnavailableWithoutRetryAfter_IsNotThrottling()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        Assert.False(ProviderThrottleSignal.IsThrottled(response));
    }

    [Fact]
    public void IsThrottled_ThrottleWrappedInAnotherFailure_IsThrottling()
    {
        var wrapped = new InvalidOperationException(
            "Listing failed.",
            new HttpRequestException("Too many requests.", null, HttpStatusCode.TooManyRequests));

        Assert.True(ProviderThrottleSignal.IsThrottled(wrapped));
    }
}
