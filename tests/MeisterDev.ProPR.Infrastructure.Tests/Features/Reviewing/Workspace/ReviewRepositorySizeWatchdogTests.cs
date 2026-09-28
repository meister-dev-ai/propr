// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

/// <summary>
///     What the repository-size watch does to a command that is still running. The command stands in for git
///     here: what matters is which of the two cancellations reaches the caller, and a real fetch decides that
///     no differently than a task waiting on the same token.
/// </summary>
public sealed class ReviewRepositorySizeWatchdogTests : IDisposable
{
    private readonly string _watchedDirectory = Path.Combine(
        Path.GetTempPath(),
        "review-size-watchdog-" + Guid.NewGuid().ToString("N"));

    public ReviewRepositorySizeWatchdogTests()
    {
        Directory.CreateDirectory(this._watchedDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._watchedDirectory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary directory outlives the test run, which costs the run nothing.
        }
    }

    [Fact]
    public async Task RunAsync_WhenTheDirectoryPassesTheBound_StopsTheCommandAndReportsBothNumbers()
    {
        await File.WriteAllBytesAsync(Path.Combine(this._watchedDirectory, "pack"), new byte[3 * 1024 * 1024]);
        var watchdog = new ReviewRepositorySizeWatchdog(
            this._watchedDirectory,
            limitMegabytes: 1,
            TimeSpan.FromMilliseconds(5),
            "fetch");

        var breach = await Assert.ThrowsAsync<ReviewRepositorySizeExceededException>(() => watchdog.RunAsync(WaitForCancellationAsync, CancellationToken.None));

        Assert.Equal("fetch", breach.Stage);
        Assert.Equal(1, breach.LimitMegabytes);
        Assert.Equal(3, breach.MeasuredMegabytes);
    }

    // A transfer that starts and ends between two samples is measured after it has returned, so a bound
    // holds for a repository that arrives faster than the sampling interval.
    [Fact]
    public async Task RunAsync_WhenTheCommandEndsBeforeTheFirstSample_StillReportsTheBreach()
    {
        var watchdog = new ReviewRepositorySizeWatchdog(
            this._watchedDirectory,
            limitMegabytes: 1,
            TimeSpan.FromMinutes(5),
            "fetch");

        var breach = await Assert.ThrowsAsync<ReviewRepositorySizeExceededException>(() => watchdog.RunAsync(
            async _ =>
            {
                await File.WriteAllBytesAsync(Path.Combine(this._watchedDirectory, "pack"), new byte[3 * 1024 * 1024]);
                return new GitCommandResult(0, "done", string.Empty);
            },
            CancellationToken.None));

        Assert.Equal("fetch", breach.Stage);
        Assert.Equal(1, breach.LimitMegabytes);
        Assert.Equal(3, breach.MeasuredMegabytes);
    }

    // A caller that cancels gets its own cancellation back. Reported as a size breach, the job would carry a
    // refusal naming a repository nothing measured to the end.
    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_ReportsTheCancellationAndNoBreach()
    {
        var watchdog = new ReviewRepositorySizeWatchdog(
            this._watchedDirectory,
            limitMegabytes: 1,
            TimeSpan.FromMilliseconds(5),
            "fetch");
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchdog.RunAsync(WaitForCancellationAsync, caller.Token));
    }

    [Fact]
    public async Task RunAsync_WhileTheDirectoryStaysUnderTheBound_ReturnsWhatTheCommandReported()
    {
        await File.WriteAllBytesAsync(Path.Combine(this._watchedDirectory, "pack"), new byte[512 * 1024]);
        var watchdog = new ReviewRepositorySizeWatchdog(
            this._watchedDirectory,
            limitMegabytes: 8,
            TimeSpan.FromMilliseconds(5),
            "fetch");

        var result = await watchdog.RunAsync(
            async token =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                return new GitCommandResult(0, "done", string.Empty);
            },
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", result.StandardOutput);
    }

    /// <summary>A command that runs until its token is cancelled, as a fetch of a large repository does.</summary>
    /// <param name="ct">The token the watch cancels.</param>
    private static async Task<GitCommandResult> WaitForCancellationAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new GitCommandResult(0, string.Empty, string.Empty);
    }
}
