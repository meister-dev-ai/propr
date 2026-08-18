// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsPingClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly UsageStatisticsSnapshot Snapshot = new()
    {
        SchemaVersion = UsageStatisticsContract.SchemaVersion,
        InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ProductVersion = "1.0.0.alpha.0049",
        Edition = UsageStatisticsEdition.Community,
        ActiveUsers = "2-5",
        PullRequestsPerWeek = "1-20",
        FindingsRaisedPerWeek = "1-50",
    };

    // The endpoint is a compile-time constant rather than a setting, so an installation cannot be pointed
    // elsewhere. This case pins the value the payload is posted to.
    [Fact]
    public async Task ASnapshot_IsPostedToThePublishedEndpointAsJson()
    {
        HttpRequestMessage? captured = null;
        string? body = null;

        var handler = new StubHttpMessageHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var outcome = await CreateClient(handler).SendAsync(Snapshot);

        Assert.True(outcome.Succeeded);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(UsageStatisticsContract.PingEndpoint, captured.RequestUri!.ToString());
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);

        using var parsed = JsonDocument.Parse(body!);
        Assert.Equal("community", parsed.RootElement.GetProperty("edition").GetString());
        Assert.Equal("2-5", parsed.RootElement.GetProperty("activeUsers").GetString());
    }

    [Fact]
    public async Task AnAnswerCarryingAnAdvisory_IsReadBack()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "schemaVersion": 1,
                      "latestVersion": "1.0.0.alpha.0050",
                      "advisories": [
                        { "id": "PROPR-2026-0001", "severity": "high", "link": "https://example.invalid/a" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));

        var outcome = await CreateClient(handler).SendAsync(Snapshot);

        Assert.True(outcome.Succeeded);
        Assert.Equal("1.0.0.alpha.0050", outcome.Response!.LatestVersion);
        Assert.Equal("PROPR-2026-0001", Assert.Single(outcome.Response.Advisories).Id);
    }

    // The response comes from a service the installation does not control, so a shape this build has never
    // seen must not fail the send.
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"somethingNewNobodyHasShippedYet": 42}""")]
    public async Task AnAnswerThisBuildCannotUse_LeavesTheSendSuccessful(string payload)
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }));

        var outcome = await CreateClient(handler).SendAsync(Snapshot);

        Assert.True(outcome.Succeeded);
    }

    // A failure is returned as a recorded outcome rather than an exception, so it cannot surface to an
    // operator or interrupt the loop.
    [Fact]
    public async Task AnUnreachableReceiver_ReportsAFailureRatherThanThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("no route"));

        var outcome = await CreateClient(handler).SendAsync(Snapshot);

        Assert.False(outcome.Succeeded);
        Assert.Equal("The receiver could not be reached.", outcome.Detail);
        Assert.Null(outcome.Response);
    }

    [Fact]
    public async Task AReceiverThatRefusesThePayload_ReportsTheStatusWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));

        var outcome = await CreateClient(handler).SendAsync(Snapshot);

        Assert.False(outcome.Succeeded);
        Assert.Contains("400", outcome.Detail, StringComparison.Ordinal);
    }

    // An unbounded response would allocate without limit on the installation's own host.
    [Fact]
    public async Task AnUnboundedAnswer_IsReadOnlyUpToItsCeiling()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 5 * 1024 * 1024), Encoding.UTF8, "application/json"),
            }));

        var outcome = await CreateClient(handler).SendAsync(Snapshot);

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Response);
    }

    private static UsageStatisticsPingClient CreateClient(HttpMessageHandler handler)
    {
        return new UsageStatisticsPingClient(
            new HttpClient(handler),
            new FakeTimeProvider(Now),
            NullLogger<UsageStatisticsPingClient>.Instance);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}
