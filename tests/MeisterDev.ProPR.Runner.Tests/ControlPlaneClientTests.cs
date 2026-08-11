// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     Which control plane a lease call reaches. The heartbeat and the release drop per-lease state that only
///     the granting replica holds, so when the manifest names that replica, routing these anywhere else
///     succeeds in the database and leaks the rest with no record.
/// </summary>
public sealed class ControlPlaneClientTests
{
    private static readonly Guid JobId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    [Fact]
    public async Task AHeartbeat_WithoutAnAdvertisedReplica_UsesTheConfiguredControlPlane()
    {
        var handler = new CapturingHandler("""{"accepted":true,"expiresAt":null,"stopReason":"None"}""");
        var client = Create(handler);

        await client.HeartbeatAsync(JobId, 3, CancellationToken.None);

        Assert.Equal("control-plane.invalid", Assert.Single(handler.Requests).Host);
    }

    // The heartbeat was the one runner operation that carried no version, which let a mid-flight deploy
    // hide behind a healthy lease while every execution call was refused for skew.
    [Fact]
    public async Task AHeartbeat_SaysWhichContractVersionItSpeaks()
    {
        var handler = new CapturingHandler("""{"accepted":true,"expiresAt":null,"stopReason":"None"}""");
        var client = Create(handler);

        await client.HeartbeatAsync(JobId, 3, CancellationToken.None);

        var body = System.Text.Json.JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(Contracts.RunnerContractVersion.Current, body.RootElement.GetProperty("contractVersion").GetInt32());
    }

    // A version refusal differs from a transient outage: this control plane can no longer serve this
    // runner's calls at all, so the answer is a lost lease with the skew named, and the review stops now.
    [Fact]
    public async Task AVersionRefusedHeartbeat_ReadsAsALostLeaseRatherThanAnOutage()
    {
        var handler = new CapturingHandler(
            """{"code":"unsupported_contract_version","message":"too new"}""",
            HttpStatusCode.Conflict);
        var client = Create(handler);

        var beat = await client.HeartbeatAsync(JobId, 3, CancellationToken.None);

        Assert.False(beat.Held);
        Assert.False(beat.Unreachable);
        Assert.Equal("ContractRejected", beat.StopReason);
    }

    [Fact]
    public async Task AHeartbeat_ForAJobWithAnAdvertisedReplica_GoesToThatReplica()
    {
        var handler = new CapturingHandler("""{"accepted":true,"expiresAt":null,"stopReason":"None"}""");
        var client = Create(handler);

        await client.HeartbeatAsync(JobId, 3, CancellationToken.None, "https://replica-2.invalid");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("replica-2.invalid", request.Host);
        Assert.Equal("/runners/lease/heartbeat", request.AbsolutePath);
    }

    [Fact]
    public async Task ARelease_ForAJobWithAnAdvertisedReplica_GoesToThatReplica()
    {
        var handler = new CapturingHandler("""{"released":true}""");
        var client = Create(handler);

        await client.ReleaseLeaseAsync(JobId, 3, CancellationToken.None, "https://replica-2.invalid");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("replica-2.invalid", request.Host);
        Assert.Equal("/runners/lease/release", request.AbsolutePath);
    }

    private static ControlPlaneClient Create(CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://control-plane.invalid/") };
        return new ControlPlaneClient(http, NullLogger<ControlPlaneClient>.Instance);
    }

    private sealed class CapturingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request.RequestUri!);
            this.Bodies.Add(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
