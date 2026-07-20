// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Profiling harness that quantifies the backend-memory cost of reviewing a large synthetic
///     pull request. Heavy paths are gated behind the MEASURE_HEAVY=1 environment variable so the
///     ordinary test suite stays fast; the prompt-scaling measurement is always cheap.
///     Results are written to $MEASURE_OUT (default: obj/large-pr-memory.txt) and the test output.
/// </summary>
public sealed class LargePrMemoryProfilingTests(ITestOutputHelper output)
{
    // Synthetic large-PR profile. Mirrors an observed ~194-file large PR: a mix of small, medium,
    // and large source files. Sizes are in characters (UTF-16 => ~2 bytes/char on the managed heap).
    private const int TotalFiles = 194;

    private static readonly (int Count, int FullContentChars, int DiffChars, string Kind)[] FileMix =
    [
        (120, 6_000, 1_500, "small"), // ~200 LOC source files, modest diffs
        (54, 20_000, 6_000, "medium"), // ~600 LOC files, substantial diffs
        (18, 60_000, 25_000, "large"), // generated/large files, big diffs
        (2, 200_000, 90_000, "huge"), // pathological large files (lockfiles/generated)
    ];

    [Fact]
    public void PrWidePlanningPrompt_ScalesWithFileCountAndDiffSize()
    {
        var report = new StringBuilder();

        void Line(string s)
        {
            report.AppendLine(s);
            output.WriteLine(s);
        }

        Line("=== (1b) PR-wide planning prompt blow-up ===");
        Line($"Synthetic profile: {DescribeProfile()}");
        Line("files | prompt_chars | prompt_MB_utf16 | chars_per_file");

        foreach (var fileCount in new[] { 1, 10, 50, 100, TotalFiles })
        {
            var pr = BuildSyntheticPr(fileCount);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var message = ReviewPrompts.BuildPrWidePlanningUserMessage(pr);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var mb = message.Length * 2.0 / (1024 * 1024);
            Line(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{fileCount,5} | {message.Length,12} | {mb,15:F2} | {message.Length / fileCount,14}"));

            if (fileCount == TotalFiles)
            {
                Line(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  -> building the {TotalFiles}-file prompt once allocated {allocated / (1024.0 * 1024.0):F1} MB (transient churn)."));
                Line(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  -> retained as one string: {message.Length * 2L} bytes ({mb:F2} MB). Recorded unbounded as InputTextSample => held again per turn/pass."));
            }
        }

        WriteReport("prwide-prompt-scaling", report.ToString());

        // Sanity: the prompt must embed all files' diffs (blow-up is real, not clipped).
        var full = ReviewPrompts.BuildPrWidePlanningUserMessage(BuildSyntheticPr(TotalFiles));
        Assert.True(full.Length > 1_000_000, $"Expected a multi-MB prompt, got {full.Length} chars.");
    }

    [Fact]
    public void PullRequest_RetainedManagedHeap_ScalesWithFileCount()
    {
        var report = new StringBuilder();

        void Line(string s)
        {
            report.AppendLine(s);
            output.WriteLine(s);
        }

        Line("=== (1a) Retained managed heap of the in-memory PullRequest object ===");
        Line($"Synthetic profile: {DescribeProfile()}");
        Line("files | retained_heap_MB (GC.GetTotalMemory(true) delta)");

        foreach (var fileCount in new[] { 0, 50, 100, TotalFiles })
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var baseline = GC.GetTotalMemory(true);
            var pr = BuildSyntheticPr(fileCount);
            var withPr = GC.GetTotalMemory(true);
            GC.KeepAlive(pr);
            var deltaMb = (withPr - baseline) / (1024.0 * 1024.0);
            Line(string.Create(CultureInfo.InvariantCulture, $"{fileCount,5} | {deltaMb,10:F2}"));
        }

        WriteReport("pr-retained-heap", report.ToString());

        Assert.True(report.Length > 0, "Profile test should have produced output rows.");
    }

    [Fact]
    public async Task FileByFileReview_OfLargePr_MemoryProfile()
    {
        if (Environment.GetEnvironmentVariable("MEASURE_HEAVY") != "1")
        {
            output.WriteLine("Skipped: set MEASURE_HEAVY=1 to run the full orchestrator memory profile.");
            return;
        }

        var report = new StringBuilder();

        void Line(string s)
        {
            report.AppendLine(s);
            output.WriteLine(s);
        }

        Line("=== (1) FileByFile orchestrator memory profile over a large synthetic PR ===");
        Line($"Synthetic profile: {DescribeProfile()}");

        var jobRepository = new InMemoryReviewJobRepository();
        var protocolRecorder = new InMemoryProtocolRecorder(jobRepository);

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1)
        {
            Status = JobStatus.Processing,
        };
        await jobRepository.AddAsync(job);

        // Stubbed model: every call returns a "done" completion so each file's agentic loop ends in
        // one turn. This isolates the memory cost of prompt building + full-text protocol retention.
        const string completion =
            "{\"summary\":\"done\",\"comments\":[],\"confidence_evaluations\":[{\"concern\":\"correctness\",\"confidence\":90}],\"investigation_complete\":true,\"loop_complete\":true}";
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, completion)));

        var options = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions());
        var aiCore = new ToolAwareAiReviewCore(chatClient, options, NullLogger<ToolAwareAiReviewCore>.Instance);
        var orchestrator = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            chatClient,
            options,
            NullLogger<FileByFileReviewOrchestrator>.Instance);

        var pr = BuildSyntheticPr(TotalFiles);
        var context = new ReviewSystemContext(null, [], null);

        var process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        process.Refresh();
        var wsBefore = process.WorkingSet64;

        // Sample peak working set + managed heap on a background thread during the run.
        long peakWorkingSet = wsBefore;
        long peakHeap = heapBefore;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                try
                {
                    await Task.Delay(25, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        var sw = Stopwatch.StartNew();
        await orchestrator.ReviewAsync(job, pr, context, CancellationToken.None);
        sw.Stop();
        await stop.CancelAsync();
        await sampler;

        var allocDuring = GC.GetTotalAllocatedBytes(true) - allocBefore;
        var heapAfterRun = GC.GetTotalMemory(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapAfterGc = GC.GetTotalMemory(true);
        process.Refresh();
        var wsAfter = process.WorkingSet64;

        // How much did the in-memory protocol retain (production recorder stores full text unbounded)?
        var reloaded = await jobRepository.GetByIdWithProtocolsAsync(job.Id);
        var protocolCount = reloaded?.Protocols.Count ?? 0;
        var eventCount = reloaded?.Protocols.Sum(p => p.Events.Count) ?? 0;
        long retainedText = reloaded?.Protocols
            .SelectMany(p => p.Events)
            .Sum(e => (long)((e.InputTextSample?.Length ?? 0) + (e.SystemPrompt?.Length ?? 0) + (e.OutputSummary?.Length ?? 0))) ?? 0;

        double Mb(long b) => b / (1024.0 * 1024.0);
        Line(string.Create(CultureInfo.InvariantCulture, $"files reviewed          : {pr.ChangedFiles.Count}"));
        Line(string.Create(CultureInfo.InvariantCulture, $"wall clock              : {sw.ElapsedMilliseconds} ms"));
        Line(string.Create(CultureInfo.InvariantCulture, $"managed heap before     : {Mb(heapBefore):F1} MB"));
        Line(string.Create(CultureInfo.InvariantCulture, $"managed heap peak (live): {Mb(peakHeap):F1} MB"));
        Line(string.Create(CultureInfo.InvariantCulture, $"managed heap after run  : {Mb(heapAfterRun):F1} MB"));
        Line(string.Create(CultureInfo.InvariantCulture, $"managed heap after GC   : {Mb(heapAfterGc):F1} MB  (retained)"));
        Line(string.Create(CultureInfo.InvariantCulture, $"total allocated (churn) : {Mb(allocDuring):F1} MB"));
        Line(string.Create(CultureInfo.InvariantCulture, $"working set before      : {Mb(wsBefore):F1} MB"));
        Line(string.Create(CultureInfo.InvariantCulture, $"working set peak         : {Mb(peakWorkingSet):F1} MB"));
        Line(string.Create(CultureInfo.InvariantCulture, $"working set after       : {Mb(wsAfter):F1} MB"));
        Line(string.Create(CultureInfo.InvariantCulture, $"protocols / events      : {protocolCount} / {eventCount}"));
        Line(
            string.Create(
                CultureInfo.InvariantCulture,
                $"retained protocol text  : {Mb(retainedText * 2L):F1} MB (unbounded InputTextSample/SystemPrompt/OutputSummary, UTF-16)"));

        WriteReport("filebyfile-memory", report.ToString());

        Assert.True(report.Length > 0, "Profile test should have produced output rows.");
    }

    private static string DescribeProfile()
    {
        var totalContent = FileMix.Sum(m => (long)m.Count * m.FullContentChars);
        var totalDiff = FileMix.Sum(m => (long)m.Count * m.DiffChars);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{TotalFiles} files; sum(FullContent)={totalContent / (1024.0 * 1024.0):F1} MB chars, sum(UnifiedDiff)={totalDiff / (1024.0 * 1024.0):F1} MB chars; mix={string.Join(", ", FileMix.Select(m => $"{m.Count}x{m.Kind}"))}");
    }

    private static PullRequest BuildSyntheticPr(int fileCount)
    {
        var files = new List<ChangedFile>(fileCount);
        var index = 0;
        foreach (var (count, contentChars, diffChars, kind) in FileMix)
        {
            for (var i = 0; i < count && index < fileCount; i++, index++)
            {
                var path = string.Create(CultureInfo.InvariantCulture, $"src/module{index / 20}/{kind}_file_{index}.cs");
                files.Add(
                    new ChangedFile(
                        path,
                        ChangeType.Edit,
                        BuildContent(index, contentChars),
                        BuildDiff(index, diffChars)));
            }
        }

        // Top up with small files if the mix ran out before reaching fileCount.
        while (index < fileCount)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"src/extra/file_{index}.cs");
            files.Add(new ChangedFile(path, ChangeType.Edit, BuildContent(index, 6_000), BuildDiff(index, 1_500)));
            index++;
        }

        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            4242,
            1,
            "Large refactor across many modules",
            "Synthetic large PR used for backend memory profiling.",
            "feature/large",
            "main",
            files.AsReadOnly());
    }

    private static string BuildContent(int seed, int chars)
    {
        var sb = new StringBuilder(chars + 64);
        var line = 0;
        while (sb.Length < chars)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    var value{line} = Compute{seed}({line} * 31 + {seed}); // line {line}\n");
            line++;
        }

        return sb.ToString();
    }

    private static string BuildDiff(int seed, int chars)
    {
        var sb = new StringBuilder(chars + 128);
        sb.Append(CultureInfo.InvariantCulture, $"@@ -1,{chars / 40} +1,{chars / 40} @@\n");
        var line = 0;
        while (sb.Length < chars)
        {
            // Alternate context / added / removed lines to look like a real unified diff.
            var marker = (line % 3) switch { 0 => " ", 1 => "+", _ => "-" };
            sb.Append(CultureInfo.InvariantCulture, $"{marker}    var value{line} = Compute{seed}({line} * 31 + {seed});\n");
            line++;
        }

        return sb.ToString();
    }

    private static void WriteReport(string tag, string content)
    {
        var target = Environment.GetEnvironmentVariable("MEASURE_OUT");
        var path = string.IsNullOrWhiteSpace(target)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, $"measure-{tag}.txt")
            : target;
        try
        {
            System.IO.File.AppendAllText(path, content + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[measure] could not write {path}: {ex.Message}");
        }
    }
}
