// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Runner.Execution;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     What each answer from the control plane means. The states must stay distinct: a refusal means this
///     executor lost the job, not-offered means the installation has no such tool, and a fault means the call
///     never received an answer. Collapsing a fault into not-offered reported to the reviewer, during a
///     rolling restart, that the pull request changed no files, and the reviewer acted on that.
/// </summary>
public sealed class HttpRunnerToolProxyTests
{
    private static readonly RunnerCallContext Call = new(Guid.NewGuid(), 3, "runner-a");

    [Fact]
    public async Task AServedAnswer_CarriesTheValue()
    {
        var proxy = Create(Respond(HttpStatusCode.OK, """{"unavailable":false,"value":[]}"""));

        var result = await proxy.GetChangedFilesAsync(Call);

        Assert.True(result.IsServed);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task AControlPlaneThatDoesNotOfferTheTool_SaysSoExplicitly()
    {
        var proxy = Create(Respond(HttpStatusCode.OK, """{"unavailable":true,"value":null}"""));

        var result = await proxy.GetChangedFilesAsync(Call);

        Assert.True(result.Unavailable);
        Assert.Null(result.Fault);
    }

    [Fact]
    public async Task ALostLease_IsARefusalNotAFault()
    {
        var proxy = Create(Respond(HttpStatusCode.Conflict, """{"code":"lease_not_held","message":"gone"}"""));

        var result = await proxy.GetChangedFilesAsync(Call);

        Assert.Equal(RunnerCallRefusal.NotTheLeaseHolder, result.Refusal);
        Assert.Null(result.Fault);
    }

    // Only the control plane's own envelope may report that a tool is not offered. A 502 comes from an
    // intermediate proxy during a restart, and it has to be reported as a retryable tool error.
    [Fact]
    public async Task AServerError_IsAFaultNeverNotOffered()
    {
        var proxy = Create(Respond(HttpStatusCode.BadGateway, "upstream unavailable"));

        var result = await proxy.GetChangedFilesAsync(Call);

        Assert.False(result.Unavailable);
        Assert.NotNull(result.Fault);
        Assert.Contains("502", result.Fault, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADroppedConnection_IsAFault()
    {
        var proxy = Create(new ThrowingHandler());

        var result = await proxy.GetChangedFilesAsync(Call);

        Assert.False(result.Unavailable);
        Assert.NotNull(result.Fault);
    }

    private static HttpRunnerToolProxy Create(HttpMessageHandler handler)
    {
        return new HttpRunnerToolProxy(new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/runners/execution/") });
    }

    private static StubHandler Respond(HttpStatusCode status, string body)
    {
        return new StubHandler(status, body);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused");
        }
    }
}
