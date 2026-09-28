// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

/// <summary>
///     Runs one git command while measuring how large a directory grows, and stops the command once that
///     directory passes the client's repository-size bound.
/// </summary>
/// <remarks>
///     The bound is applied where the bytes arrive. No SCM host reports the size of a repository before it is
///     transferred, and a transfer that ran to completion before the size was weighed would already have spent
///     the disk and the time the bound exists to save.
///     <para>
///         The command is stopped through a cancellation source linked to the caller's token.
///         <see cref="GitCommandRunner" /> kills the process tree it started when its token is cancelled, so
///         git has stopped writing by the time the caller removes the directory.
///     </para>
/// </remarks>
/// <param name="watchedDirectory">The directory git writes the transferred data into.</param>
/// <param name="limitMegabytes">The client's bound, in mebibytes (1,048,576 bytes).</param>
/// <param name="samplingInterval">How long to wait between two measurements.</param>
/// <param name="stage">The preparation step the command belongs to, reported on the failure.</param>
internal sealed class ReviewRepositorySizeWatchdog(
    string watchedDirectory,
    int limitMegabytes,
    TimeSpan samplingInterval,
    string stage)
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    private long _measuredBytes;

    /// <summary>
    ///     Runs <paramref name="command" /> and returns what it reported, or throws
    ///     <see cref="ReviewRepositorySizeExceededException" /> when the watched directory passed the bound.
    /// </summary>
    /// <param name="command">The command, started with the token this watch cancels.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public async Task<GitCommandResult> RunAsync(
        Func<CancellationToken, Task<GitCommandResult>> command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var breach = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var commandEnded = new CancellationTokenSource();
        var watching = this.WatchAsync(breach, commandEnded.Token);

        try
        {
            var result = await command(breach.Token).ConfigureAwait(false);

            // The samples decide when a running transfer is stopped; they do not decide whether the bound
            // holds. A transfer that started and finished between two samples was never measured, and a
            // sample can pass the bound while the command is finishing. The directory is therefore measured
            // once more here, after the command has stopped writing, and a size over the bound is the same
            // breach as one a sample found.
            this.MeasureOnce();
            this.ThrowIfBreached();
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled by this watch and not by the caller. A caller that cancelled at the same moment keeps
            // its own cancellation: its job is over either way, and reporting a size refusal for it would
            // record a decision about the repository that nothing measured to the end.
            this.ThrowIfBreached();
            throw;
        }
        finally
        {
            await commandEnded.CancelAsync().ConfigureAwait(false);
            await watching.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Throws <see cref="ReviewRepositorySizeExceededException" /> when <paramref name="directory" />
    ///     already occupies more than <paramref name="limitMegabytes" />.
    /// </summary>
    /// <param name="directory">The directory to measure.</param>
    /// <param name="limitMegabytes">The client's bound, in mebibytes.</param>
    /// <param name="stage">The preparation step the refusal is reported for.</param>
    /// <remarks>
    ///     A caller uses this before it transfers anything into a directory that is already on disk. The
    ///     measurement reads what lies there at this moment and stores nothing, so a repository that has since
    ///     become smaller is transferred into again on the next review.
    /// </remarks>
    internal static void ThrowIfDirectoryPassesBound(string directory, int limitMegabytes, string stage)
    {
        var bytes = MeasureDirectoryBytes(directory);
        if (bytes > (long)limitMegabytes * BytesPerMegabyte)
        {
            throw new ReviewRepositorySizeExceededException(stage, ToMegabytes(bytes), limitMegabytes);
        }
    }

    /// <summary>
    ///     Returns how much disk the directory occupies, as the sum of the sizes of the files under it.
    /// </summary>
    /// <param name="path">The directory to measure.</param>
    /// <remarks>
    ///     A directory being written to changes under the enumeration, so a file that has since been replaced
    ///     or removed contributes nothing instead of failing the measurement. The next sample sees the
    ///     directory as it then is.
    /// </remarks>
    internal static long MeasureDirectoryBytes(string path)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,

            // Hidden and system files are included, because git writes both and they occupy the disk like any
            // other file. Symbolic links are skipped: a link's target is counted where it lies, and one
            // pointing at a directory above it would not terminate.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var total = 0L;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", enumeration))
            {
                try
                {
                    total += file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The file was replaced or removed between the listing and the read of its size.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The directory does not exist yet, or is being rewritten. It occupies nothing that can be
            // measured at this sample.
        }

        return total;
    }

    /// <summary>The size in mebibytes, rounded up, so a directory just past the bound is not reported as at it.</summary>
    /// <param name="bytes">The measured size.</param>
    private static int ToMegabytes(long bytes)
    {
        return (int)Math.Min(int.MaxValue, (bytes + BytesPerMegabyte - 1) / BytesPerMegabyte);
    }

    /// <summary>
    ///     Measures the watched directory until the command ends, and cancels the command at the first sample
    ///     over the bound.
    /// </summary>
    /// <param name="breach">The source the command runs under.</param>
    /// <param name="commandEnded">Cancelled by the caller once the command has returned.</param>
    private async Task WatchAsync(CancellationTokenSource breach, CancellationToken commandEnded)
    {
        while (!commandEnded.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(samplingInterval, commandEnded).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var bytes = MeasureDirectoryBytes(watchedDirectory);
            if (bytes <= (long)limitMegabytes * BytesPerMegabyte)
            {
                continue;
            }

            Volatile.Write(ref this._measuredBytes, bytes);
            await breach.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    /// <summary>Measures the watched directory and records a size over the bound as the breach.</summary>
    /// <remarks>
    ///     A breach a sample already recorded is kept, because that measurement was taken while the transfer
    ///     was still writing and the caller removes the directory once this returns.
    /// </remarks>
    private void MeasureOnce()
    {
        if (Volatile.Read(ref this._measuredBytes) > 0)
        {
            return;
        }

        var bytes = MeasureDirectoryBytes(watchedDirectory);
        if (bytes > (long)limitMegabytes * BytesPerMegabyte)
        {
            Volatile.Write(ref this._measuredBytes, bytes);
        }
    }

    private void ThrowIfBreached()
    {
        var bytes = Volatile.Read(ref this._measuredBytes);
        if (bytes > 0)
        {
            throw new ReviewRepositorySizeExceededException(stage, ToMegabytes(bytes), limitMegabytes);
        }
    }
}

/// <summary>
///     Thrown when workspace preparation stopped a transfer because the repository passed the client's
///     repository-size bound.
/// </summary>
/// <param name="stage">The preparation step the transfer belongs to.</param>
/// <param name="measuredMegabytes">The size measured at the sample that passed the bound.</param>
/// <param name="limitMegabytes">The client's bound.</param>
internal sealed class ReviewRepositorySizeExceededException(string stage, int measuredMegabytes, int limitMegabytes)
    : Exception($"The repository reached {measuredMegabytes} MB during {stage} and passes the limit of {limitMegabytes} MB.")
{
    /// <summary>The preparation step that was stopped.</summary>
    public string Stage { get; } = stage;

    /// <summary>The size measured when the transfer was stopped. The repository is at least this large.</summary>
    public int MeasuredMegabytes { get; } = measuredMegabytes;

    /// <summary>The client's bound.</summary>
    public int LimitMegabytes { get; } = limitMegabytes;
}
