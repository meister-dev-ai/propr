// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Measures the managed-heap footprint of loading a seeded active job's protocol graph the way the
///     review-history full-trace reader (GetByIdWithProtocolsAsync) does, comparing change-tracked vs
///     AsNoTracking on the identical Include/ThenInclude query (the fix flips this query to AsNoTracking).
///     Reports the deterministic materialized payload held by the graph, the change-tracker entry count,
///     and a K-amplified total-heap delta so the tracking overhead is visible above GC noise.
///     Opt-in: requires MEASURE_DB_CONN (a Postgres connection string) pointed at a DB seeded with the
///     crash-relief scripts. Never runs in the ordinary suite.
/// </summary>
public sealed class PollProtocolLoadHeapTests(ITestOutputHelper output)
{
    private const int AmplifyCopies = 3;

    [Fact]
    public async Task GetByIdWithProtocols_Tracked_vs_NoTracking_HeapFootprint()
    {
        var conn = Environment.GetEnvironmentVariable("MEASURE_DB_CONN");
        if (string.IsNullOrWhiteSpace(conn))
        {
            output.WriteLine("Skipped: set MEASURE_DB_CONN to run the protocol-load heap measurement.");
            return;
        }

        var jobTitle = Environment.GetEnvironmentVariable("MEASURE_JOB_TITLE") ?? "Active active-large";
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(conn, o => o.UseVector())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        Guid jobId;
        await using (var probe = new MeisterProPRDbContext(options))
        {
            jobId = await probe.ReviewJobs.AsNoTracking()
                .Where(j => j.PrTitle == jobTitle)
                .Select(j => j.Id)
                .FirstAsync();
        }

        // Warm connection pool + model + JIT so measured runs do not pay one-time costs.
        await using (var warm = new MeisterProPRDbContext(options))
        {
            _ = await Query(warm, tracked: false).FirstOrDefaultAsync(j => j.Id == jobId);
        }

        double Mb(long b) => b / (1024.0 * 1024.0);
        void Line(FormattableString s) => output.WriteLine(FormattableString.Invariant(s));

        // --- Deterministic payload held by a single loaded graph (identical for both query modes,
        //     since the heavy query selects the same columns) + tracked entry count. ---
        long payloadBytes;
        int trackedEntries;
        int protocols;
        int events;
        await using (var ctx = new MeisterProPRDbContext(options))
        {
            var job = await Query(ctx, tracked: true).FirstOrDefaultAsync(j => j.Id == jobId);
            protocols = job!.Protocols.Count;
            events = job.Protocols.Sum(p => p.Events.Count);
            payloadBytes = PayloadChars(job) * 2L; // UTF-16
            trackedEntries = ctx.ChangeTracker.Entries().Count();
        }

        int noTrackingEntries;
        await using (var ctx = new MeisterProPRDbContext(options))
        {
            var job = await Query(ctx, tracked: false).FirstOrDefaultAsync(j => j.Id == jobId);
            noTrackingEntries = ctx.ChangeTracker.Entries().Count();
            GC.KeepAlive(job);
        }

        // --- K-amplified total live heap: hold AmplifyCopies graphs (each in its own context) so the
        //     retained heap dwarfs GC noise; the tracked-vs-no-tracking gap is AmplifyCopies x the
        //     per-load change-tracking overhead. ---
        var trackedHeap = await AmplifiedHeapAsync(options, jobId, tracked: true);
        var noTrackingHeap = await AmplifiedHeapAsync(options, jobId, tracked: false);

        Line($"=== (b) Protocol-load managed-heap footprint: tracked vs AsNoTracking ===");
        Line($"job: '{jobTitle}'  protocols={protocols}  events={events}");
        Line(
            $"deterministic materialized payload held by ONE loaded graph : {Mb(payloadBytes):F1} MB (input_text_sample + system_prompt + output_summary + phase_timings strings, UTF-16) -- SAME in both modes");
        Line($"change-tracker entries: tracked={trackedEntries}  AsNoTracking={noTrackingEntries}");
        Line($"live heap holding {AmplifyCopies} graphs: tracked={Mb(trackedHeap):F1} MB  AsNoTracking={Mb(noTrackingHeap):F1} MB");
        Line(
            $"  => per-load: tracked={Mb(trackedHeap / AmplifyCopies):F1} MB  AsNoTracking={Mb(noTrackingHeap / AmplifyCopies):F1} MB | tracking overhead ~{Mb((trackedHeap - noTrackingHeap) / AmplifyCopies):F1} MB/load");

        var outFile = Environment.GetEnvironmentVariable("MEASURE_OUT");
        if (!string.IsNullOrWhiteSpace(outFile))
        {
            System.IO.File.AppendAllText(
                outFile,
                FormattableString.Invariant(
                    $"payload_mb={Mb(payloadBytes):F1} tracked_entries={trackedEntries} notracking_entries={noTrackingEntries} tracked_perload_mb={Mb(trackedHeap / AmplifyCopies):F1} notracking_perload_mb={Mb(noTrackingHeap / AmplifyCopies):F1} events={events}{Environment.NewLine}"));
        }

        Assert.Equal(0, noTrackingEntries);
        Assert.True(trackedEntries > 0);
    }

    private static long PayloadChars(ReviewJob job) =>
        job.Protocols.SelectMany(p => p.Events).Sum(e =>
            (long)(e.InputTextSample?.Length ?? 0)
            + (e.SystemPrompt?.Length ?? 0)
            + (e.OutputSummary?.Length ?? 0)
            + (e.Error?.Length ?? 0)
            + (e.PhaseTimings?.Sum(pt =>
                (long)(pt.Summary?.Length ?? 0) + pt.Name.Length + pt.DisplayName.Length
                + pt.Availability.Length + pt.Outcome.Length) ?? 0));

    private static IQueryable<ReviewJob> Query(MeisterProPRDbContext ctx, bool tracked)
    {
        var q = ctx.ReviewJobs
            .AsSplitQuery()
            .Include(j => j.Protocols.OrderByDescending(p => p.AttemptNumber))
            .ThenInclude(p => p.Events.OrderBy(e => e.OccurredAt))
            .Include(j => j.FileReviewResults);
        return tracked ? q.AsTracking() : q.AsNoTracking();
    }

    // Static roots so neither the JIT nor the GC can reclaim the held graphs before the sample.
    private static readonly List<MeisterProPRDbContext> HeldContexts = [];
    private static readonly List<ReviewJob?> HeldJobs = [];

    private static async Task<long> AmplifiedHeapAsync(DbContextOptions<MeisterProPRDbContext> options, Guid jobId, bool tracked)
    {
        HeldContexts.Clear();
        HeldJobs.Clear();
        Settle();
        var before = GC.GetTotalMemory(true);

        for (var i = 0; i < AmplifyCopies; i++)
        {
            var ctx = new MeisterProPRDbContext(options);
            HeldContexts.Add(ctx);
            HeldJobs.Add(await Query(ctx, tracked).FirstOrDefaultAsync(j => j.Id == jobId));
        }

        Settle();
        var held = GC.GetTotalMemory(true); // HeldContexts/HeldJobs are static roots -> graphs are alive

        foreach (var ctx in HeldContexts)
        {
            await ctx.DisposeAsync();
        }

        HeldContexts.Clear();
        HeldJobs.Clear();
        return held - before;
    }

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
