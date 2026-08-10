// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     What a review costs, on its way back.
///     <para>
///         The trace events this recorder ships are stored as opaque detail, so anything the control plane
///         has to act on — spend above all — must travel as its own record. A remote review that reported
///         no spend looked complete and free, and the budget the relay charges against is metered from
///         exactly these records.
///     </para>
/// </summary>
public sealed class SpoolingProtocolRecorderTests
{
    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // The pipeline accrues most of a pass's tokens at completion, not through AddTokensAsync. A recorder
    // that only watched the latter reported nothing for an ordinary review.
    [Fact]
    public async Task APassThatCompletes_ShipsWhatItSpent()
    {
        var handler = new CapturingHandler();
        var (recorder, spool) = Create(handler);

        var protocolId = await recorder.BeginAsync(JobId, 1, "file", logicalModelName: "reviewer-medium");
        await recorder.SetCompletedAsync(protocolId, "Completed", 1200, 340, 1, 0, null);
        await spool.FlushAsync(CancellationToken.None);

        var spend = Assert.Single(handler.Batches[^1].GetProperty("spend").EnumerateArray());
        Assert.Equal("reviewer-medium", spend.GetProperty("logicalModelName").GetString());
        Assert.Equal(1200, spend.GetProperty("inputTokens").GetInt64());
        Assert.Equal(340, spend.GetProperty("outputTokens").GetInt64());
    }

    // A pass that spent nothing is not a spend record. Shipping zeros would open a priced protocol per
    // stage on the control plane for stages that never called a model.
    [Fact]
    public async Task APassThatSpentNothing_ShipsNoSpend()
    {
        var handler = new CapturingHandler();
        var (recorder, spool) = Create(handler);

        var protocolId = await recorder.BeginAsync(JobId, 1, "file", logicalModelName: "reviewer-medium");
        await recorder.SetCompletedAsync(protocolId, "Completed", 0, 0, 0, 0, null);
        await spool.FlushAsync(CancellationToken.None);

        Assert.Equal(0, handler.Batches[^1].GetProperty("spend").GetArrayLength());
    }

    // Spend is attributed to the model the pass ran on, and passes on different models run concurrently.
    [Fact]
    public async Task EachPass_IsBilledToItsOwnModel()
    {
        var handler = new CapturingHandler();
        var (recorder, spool) = Create(handler);

        var cheap = await recorder.BeginAsync(JobId, 1, "triage", logicalModelName: "reviewer-fast");
        var deep = await recorder.BeginAsync(JobId, 1, "file", logicalModelName: "reviewer-deep");
        await recorder.SetCompletedAsync(deep, "Completed", 900, 100, 1, 0, null);
        await recorder.SetCompletedAsync(cheap, "Completed", 50, 10, 1, 0, null);
        await spool.FlushAsync(CancellationToken.None);

        var byModel = handler.Batches[^1].GetProperty("spend").EnumerateArray()
            .ToDictionary(s => s.GetProperty("logicalModelName").GetString()!, s => s.GetProperty("inputTokens").GetInt64());

        Assert.Equal(900, byModel["reviewer-deep"]);
        Assert.Equal(50, byModel["reviewer-fast"]);
    }

    // A protocol opened without a named model has nothing to bill. Guessing one would attribute a
    // review's cost to a model that never ran it.
    [Fact]
    public async Task APassWithNoNamedModel_ShipsNoSpend()
    {
        var handler = new CapturingHandler();
        var (recorder, spool) = Create(handler);

        var protocolId = await recorder.BeginAsync(JobId, 1, "file");
        await recorder.SetCompletedAsync(protocolId, "Completed", 500, 60, 1, 0, null);
        await spool.FlushAsync(CancellationToken.None);

        Assert.Equal(0, handler.Batches[^1].GetProperty("spend").GetArrayLength());
    }

    // The executor runs proxied memory reconsideration and synthesis-time dedup itself now. When these
    // methods threw, the first review that legitimately recorded such an event died on its own trace.
    [Fact]
    public async Task MemoryDedupAndPublicationEvents_AreSpooledLikeAnyOtherKind()
    {
        var handler = new CapturingHandler();
        var (recorder, spool) = Create(handler);

        var protocolId = await recorder.BeginAsync(JobId, 1, "file");
        await recorder.RecordMemoryEventAsync(protocolId, "memory_reconsideration_completed", "{}", null);
        await recorder.RecordDedupEventAsync(protocolId, "dedup_summary", "{}", null);
        await recorder.RecordPublicationEventAsync(protocolId, "publication_deferred", "{}", null);
        await spool.FlushAsync(CancellationToken.None);

        var names = handler.Batches[^1].GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("protocol.memory", names);
        Assert.Contains("protocol.dedup", names);
        Assert.Contains("protocol.publication", names);
    }

    // Thread passes stay in the control plane; a runner asked to open one is a bug worth failing on.
    [Fact]
    public async Task AThreadPassProtocol_IsStillRefused()
    {
        var (recorder, _) = Create(new CapturingHandler());

        await Assert.ThrowsAsync<NotSupportedException>(() => recorder.BeginForThreadPassAsync(JobId, 1));
    }

    private static (SpoolingProtocolRecorder Recorder, JobSpool Spool) Create(CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/") };
        var spool = new JobSpool(http, JobId, 1, NullLogger<JobSpool>.Instance);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero));
        return (new SpoolingProtocolRecorder(spool, time), spool);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<JsonElement> Batches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            this.Batches.Add(JsonDocument.Parse(body).RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
