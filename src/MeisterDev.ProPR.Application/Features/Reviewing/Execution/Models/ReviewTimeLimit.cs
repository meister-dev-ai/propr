// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The maximum review duration of a review's tenant, as one execution of the review observes it. The scan asks
///     it before it starts a file or a pull-request-wide pass, and records here what it left out once the time is
///     up. One instance is shared by every pass of the execution.
///     <para>
///         The deadline is counted on the monotonic clock of this process, from the moment the time left was read.
///         A wall clock that differs from the clock of the database therefore does not move the deadline, and a
///         runner counts from the moment it received the time left with its manifest.
///     </para>
/// </summary>
public sealed class ReviewTimeLimit
{
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly TimeSpan _remaining;
    private readonly List<string> _skippedFilePaths = [];
    private readonly long _startedAt;
    private bool _prWidePassesSkipped;
    private bool _reached;

    /// <summary>Starts counting the time left of a tenant's maximum review duration.</summary>
    /// <param name="maxReviewDurationSeconds">
    ///     The maximum review duration of the tenant, which ProPR writes into the summary of a review that ended at
    ///     it.
    /// </param>
    /// <param name="remaining">The time left until the deadline at this moment.</param>
    /// <param name="clock">The clock to count on; the system clock when null.</param>
    public ReviewTimeLimit(int maxReviewDurationSeconds, TimeSpan remaining, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReviewDurationSeconds, 1);

        this.MaxReviewDurationSeconds = maxReviewDurationSeconds;
        this._remaining = remaining;
        this._clock = clock ?? TimeProvider.System;
        this._startedAt = this._clock.GetTimestamp();
    }

    /// <summary>The maximum review duration of the tenant, in seconds.</summary>
    public int MaxReviewDurationSeconds { get; }

    /// <summary>The files the scan did not start because the time was up.</summary>
    public IReadOnlyList<string> SkippedFilePaths
    {
        get
        {
            lock (this._gate)
            {
                return [.. this._skippedFilePaths];
            }
        }
    }

    /// <summary>Whether the pull-request-wide passes did not run because the time was up.</summary>
    public bool PrWidePassesSkipped
    {
        get
        {
            lock (this._gate)
            {
                return this._prWidePassesSkipped;
            }
        }
    }

    /// <summary>Whether the time limit left part of the scan undone: a file or the pull-request-wide passes.</summary>
    public bool CutTheScan
    {
        get
        {
            lock (this._gate)
            {
                return this._skippedFilePaths.Count > 0 || this._prWidePassesSkipped;
            }
        }
    }

    /// <summary>Starts counting the time left of a reading, or returns null when there is no reading.</summary>
    /// <param name="reading">The maximum review duration and the time left, or null when the tenant sets none.</param>
    /// <param name="clock">The clock to count on; the system clock when null.</param>
    public static ReviewTimeLimit? Start(ReviewTimeLimitReading? reading, TimeProvider? clock = null)
    {
        return reading is null ? null : new ReviewTimeLimit(reading.MaxReviewDurationSeconds, reading.Remaining, clock);
    }

    /// <summary>
    ///     Whether the deadline has passed. Once this returns true it returns true for the rest of the execution,
    ///     so a file and the pass after it cannot see different answers.
    /// </summary>
    public bool IsReached()
    {
        lock (this._gate)
        {
            if (!this._reached && this._clock.GetElapsedTime(this._startedAt) >= this._remaining)
            {
                this._reached = true;
            }

            return this._reached;
        }
    }

    /// <summary>Records a file the scan did not start because the time was up.</summary>
    /// <param name="filePath">The path of the file.</param>
    public void RecordSkippedFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (this._gate)
        {
            this._skippedFilePaths.Add(filePath);
        }
    }

    /// <summary>Records that the pull-request-wide passes did not run because the time was up.</summary>
    public void RecordSkippedPrWidePasses()
    {
        lock (this._gate)
        {
            this._prWidePassesSkipped = true;
        }
    }

    /// <summary>
    ///     Marks a result as ended at the time limit, with the files and passes the scan left out, when the time
    ///     limit cut the scan. A result of a scan that finished before the deadline is returned unchanged.
    /// </summary>
    /// <param name="result">The result of the review.</param>
    public ReviewResult ApplyTo(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (this._gate)
        {
            if (this._skippedFilePaths.Count == 0 && !this._prWidePassesSkipped)
            {
                return result;
            }

            return result with
            {
                TimeLimitReached = true,
                TimeLimitSeconds = this.MaxReviewDurationSeconds,
                TimeLimitSkippedFilePaths = [.. this._skippedFilePaths],
                TimeLimitSkippedPrWidePasses = this._prWidePassesSkipped,
            };
        }
    }
}
