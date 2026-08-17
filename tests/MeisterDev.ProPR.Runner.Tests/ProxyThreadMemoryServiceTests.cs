// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     Thread memory over the proxy. The behaviour that matters in every case: whatever the transport does,
///     the review keeps a result. Memory degrades and the review continues, and the trace records which
///     happened.
/// </summary>
public sealed class ProxyThreadMemoryServiceTests
{
    private static readonly Guid JobId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ProtocolId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly IProtocolRecorder _recorder = Substitute.For<IProtocolRecorder>();

    [Fact]
    public async Task AReconsideredDraft_AdoptsTheAnswersSummaryAndComments()
    {
        var service = this.Create(
            Respond(
                HttpStatusCode.OK,
                """{"unavailable":false,"value":{"summary":"reconsidered","comments":[{"filePath":"src/a.cs","lineNumber":3,"severity":"warning","message":"Still real"}]}}"""));

        var result = await this.ReconsiderAsync(service, Draft());

        Assert.Equal("reconsidered", result.Summary);
        var comment = Assert.Single(result.Comments);
        Assert.Equal("Still real", comment.Message);
        Assert.Equal(CommentSeverity.Warning, comment.Severity);
    }

    // Summary and comments are what reconsideration produces. Everything else on the draft is state the
    // memory stage never touches on either side, and a wire round-trip must not blank it.
    [Fact]
    public async Task AReconsideredDraft_KeepsWhatTheMemoryStageNeverTouches()
    {
        var service = this.Create(
            Respond(
                HttpStatusCode.OK,
                """{"unavailable":false,"value":{"summary":"reconsidered","comments":[]}}"""));

        var draft = Draft() with { CarriedForwardCandidatesSkipped = 3 };
        var result = await this.ReconsiderAsync(service, draft);

        Assert.Equal(3, result.CarriedForwardCandidatesSkipped);
    }

    [Fact]
    public async Task ASuccessfulReconsideration_IsOnTheTrace()
    {
        var service = this.Create(
            Respond(
                HttpStatusCode.OK,
                """{"unavailable":false,"value":{"summary":"reconsidered","comments":[]}}"""));

        await this.ReconsiderAsync(service, Draft());

        await this._recorder.Received(1).RecordMemoryEventAsync(
            ProtocolId,
            Arg.Is("memory_reconsideration_completed"),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // A refused lease ends the review at the next relay or ingest call, and memory returns the draft
    // unchanged. What it must not do is throw, because this interface guarantees that a memory failure never
    // ends the review.
    [Fact]
    public async Task ARefusedLease_KeepsTheDraftAndSaysSo()
    {
        var service = this.Create(Respond(HttpStatusCode.Conflict, """{"error":"lease"}"""));

        var draft = Draft();
        var result = await this.ReconsiderAsync(service, draft);

        Assert.Same(draft, result);
        await this._recorder.Received(1).RecordMemoryEventAsync(
            ProtocolId, Arg.Is("memory_operation_failed"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // An older control plane answers 404 here. The review runs without memory, exactly as it did before the
    // operation existed, and the trace records the degradation rather than reporting "nothing found".
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AControlPlaneThatCannotServeMemory_DegradesWithATraceEvent(HttpStatusCode status)
    {
        var service = this.Create(Respond(status, string.Empty));

        var draft = Draft();
        var result = await this.ReconsiderAsync(service, draft);

        Assert.Same(draft, result);
        await this._recorder.Received(1).RecordMemoryEventAsync(
            ProtocolId, Arg.Is("memory_retrieval_degraded"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnInstallationWithoutAMemoryStore_DegradesWithATraceEvent()
    {
        var service = this.Create(Respond(HttpStatusCode.OK, """{"unavailable":true,"value":null}"""));

        var draft = Draft();
        var result = await this.ReconsiderAsync(service, draft);

        Assert.Same(draft, result);
        await this._recorder.Received(1).RecordMemoryEventAsync(
            ProtocolId, Arg.Is("memory_retrieval_degraded"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnreachableControlPlane_KeepsTheDraft()
    {
        var service = this.Create(new ThrowingHandler());

        var draft = Draft();
        var result = await this.ReconsiderAsync(service, draft);

        Assert.Same(draft, result);
    }

    // Without a protocol there is nowhere to put the event, and the guard mirrors every other recorder
    // call site in the pipeline.
    [Fact]
    public async Task WithoutAProtocol_NothingIsRecorded()
    {
        var service = this.Create(
            Respond(
                HttpStatusCode.OK,
                """{"unavailable":false,"value":{"summary":"reconsidered","comments":[]}}"""));

        await service.RetrieveAndReconsiderAsync(Guid.NewGuid(), Job(), "src/a.cs", null, Draft(), protocolId: null);

        Assert.Empty(this._recorder.ReceivedCalls());
    }

    // The crawl, publication, and admin paths never run on an executor. Answering them with a no-op would
    // let a later caller assume it wrote to a store this host does not have.
    [Fact]
    public async Task TheNonReviewOperations_RefuseLoudly()
    {
        var service = this.Create(Respond(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<NotSupportedException>(() => service.RecordNoOpAsync(
            Guid.NewGuid(), "https://provider.example", "project", "repo", 1, "thread", null, "resolved", "because"));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DismissFindingAsync(Guid.NewGuid(), "src/a.cs", "message", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.FindDuplicateSuppressionMatchAsync(
            Guid.NewGuid(),
            "https://provider.example",
            "project",
            "repo",
            1, "src/a.cs", "message"));
    }

    private Task<ReviewResult> ReconsiderAsync(ProxyThreadMemoryService service, ReviewResult draft)
    {
        return service.RetrieveAndReconsiderAsync(Guid.NewGuid(), Job(), "src/a.cs", "@@ -1 +1 @@", draft, ProtocolId);
    }

    private ProxyThreadMemoryService Create(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/runners/execution/") };
        return new ProxyThreadMemoryService(http, JobId, 4, this._recorder, NullLogger<ProxyThreadMemoryService>.Instance);
    }

    private static ReviewResult Draft()
    {
        return new ReviewResult(
            "draft",
            [new ReviewComment("src/a.cs", 3, CommentSeverity.Warning, "Maybe real")]);
    }

    private static ReviewJob Job()
    {
        return new ReviewJob(JobId, Guid.NewGuid(), "https://forge.invalid", "team", "repo-id", 12, 2);
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
            throw new HttpRequestException("unreachable");
        }
    }
}
