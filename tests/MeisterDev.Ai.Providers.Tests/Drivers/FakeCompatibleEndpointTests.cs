// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using System.Text;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     What the fake endpoint records, asserted because every wire test's conclusion rests on it.
/// </summary>
/// <remarks>
///     The capture reads request and content headers into one map. A name that appears in both places has to
///     keep the values from both: a capture that dropped one would let a wire test pass while the header it
///     asserts on was never sent, which is the failure a wire test exists to catch.
/// </remarks>
public sealed class FakeCompatibleEndpointTests
{
    [Fact]
    public async Task AHeaderCarriedOnBothTheRequestAndItsContentKeepsBothValues()
    {
        var wire = new FakeCompatibleEndpoint().Responds("{}");
        using var client = new HttpClient(wire);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-trace", "from-the-request");
        message.Content.Headers.TryAddWithoutValidation("x-trace", "from-the-content");

        using var response = await client.SendAsync(message);

        var headers = Assert.Single(wire.RequestHeaders);
        Assert.Equal("from-the-request, from-the-content", headers["x-trace"]);
    }

    [Fact]
    public async Task AHeaderSentSeveralTimesIsRecordedAsOneJoinedValue()
    {
        var wire = new FakeCompatibleEndpoint().Responds("{}");
        using var client = new HttpClient(wire);
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/v1/models");
        message.Headers.TryAddWithoutValidation("x-trace", "first");
        message.Headers.TryAddWithoutValidation("x-trace", "second");

        using var response = await client.SendAsync(message);

        Assert.Equal("first, second", Assert.Single(wire.RequestHeaders)["x-trace"]);
    }

    // A content header is where a client library puts the media type, so a test asserting on it reads the same
    // map as one asserting on an authentication header.
    [Fact]
    public async Task AContentHeaderIsRecordedAlongsideTheRequestHeaders()
    {
        var wire = new FakeCompatibleEndpoint().Responds("{}");
        using var client = new HttpClient(wire);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "a-key");

        using var response = await client.SendAsync(message);

        var headers = Assert.Single(wire.RequestHeaders);
        Assert.Equal("Bearer a-key", headers["Authorization"]);
        Assert.Equal("application/json; charset=utf-8", headers["Content-Type"]);
    }
}
