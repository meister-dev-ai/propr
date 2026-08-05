// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

/// <summary>
///     Each fact recorded against a pull-request scan has one owner, and the port a writer holds is what
///     enforces that. These tests fail if a narrow port grows a way to write someone else's fact, or if a
///     writer is handed a wider port than the facts it owns.
/// </summary>
public sealed class ReviewPrScanWriterSegregationTests
{
    [Fact]
    public void ThreadStatusStore_CannotWriteTheWatermarkOrTheReplyCounts()
    {
        AssertReachesExactly(
            typeof(IReviewPrScanThreadStatusStore),
            nameof(IReviewPrScanReader.GetAsync),
            nameof(IReviewPrScanThreadStatusWriter.SetLastSeenStatusesAsync));
    }

    [Fact]
    public void WatermarkStore_CannotWriteAnyPerThreadProgressOrTheThreadWatermark()
    {
        AssertReachesExactly(
            typeof(IReviewPrScanWatermarkStore),
            nameof(IReviewPrScanReader.GetAsync),
            nameof(IReviewPrScanWatermarkWriter.SetReviewWatermarkAsync));
    }

    [Fact]
    public void ThreadPassStore_CannotWriteTheReviewWatermarkOrTheLastSeenStatus()
    {
        AssertReachesExactly(
            typeof(IReviewPrScanThreadPassStore),
            nameof(IReviewPrScanReader.GetAsync),
            nameof(IReviewPrScanThreadPassWatermarkWriter.SetThreadPassWatermarkAsync),
            nameof(IReviewPrScanThreadReplyCountWriter.SetLastSeenReplyCountsAsync),
            nameof(IReviewPrScanThreadRegistry.RetainOnlyThreadsAsync));
    }

    [Fact]
    public void PendingReviewWriter_CannotReadTheRecordOrWriteAnythingElse()
    {
        AssertReachesExactly(
            typeof(IReviewPrScanPendingReviewWriter),
            nameof(IReviewPrScanPendingReviewWriter.SetPendingReviewRevisionAsync));
    }

    [Fact]
    public void FilePass_TakesOnlyTheWatermarkStore()
    {
        Assert.Equal([typeof(IReviewPrScanWatermarkStore)], ScanPortsOf(typeof(ReviewOrchestrationService)));
    }

    [Fact]
    public void ThreadPass_TakesOnlyTheThreadPassStore()
    {
        Assert.Equal([typeof(IReviewPrScanThreadPassStore)], ScanPortsOf(typeof(ThreadPassService)));
    }

    [Fact]
    public void ThreadMemoryStateMachine_TakesOnlyTheThreadStatusStore()
    {
        Assert.Equal([typeof(IReviewPrScanThreadStatusStore)], ScanPortsOf(typeof(PrCrawlService)));
    }

    [Fact]
    public void Synchronization_TakesTheThreadStatusStoreAndTheRightToRecordADeclinedRevision()
    {
        // It runs the thread-memory state machine and it owns the guard, so it owns two facts and holds one
        // port for each. Neither port reaches a watermark or any per-thread progress.
        Assert.Equal(
            [typeof(IReviewPrScanThreadStatusStore), typeof(IReviewPrScanPendingReviewWriter)],
            ScanPortsOf(typeof(PullRequestSynchronizationService)));
    }

    /// <summary>
    ///     Asserts a port reaches exactly the named operations and no others.
    /// </summary>
    /// <remarks>
    ///     The arity check is what makes the name set trustworthy. Names alone cannot tell two overloads apart,
    ///     so adding a second <c>SetLastSeenStatusesAsync</c> that wrote someone else's fact would leave the
    ///     expected set unchanged and this test would still pass. Comparing the count as well means a port can
    ///     only ever reach as many operations as are named here.
    /// </remarks>
    private static void AssertReachesExactly(Type port, params string[] expected)
    {
        var reachable = port
            .GetInterfaces()
            .Append(port)
            .SelectMany(candidate => candidate.GetMethods())
            .ToList();

        Assert.Equal(
            new HashSet<string>(expected, StringComparer.Ordinal),
            reachable.Select(method => method.Name).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(expected.Length, reachable.Count);
    }

    private static List<Type> ScanPortsOf(Type consumer)
    {
        return consumer
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.Name.Contains("ReviewPrScan", StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }
}
