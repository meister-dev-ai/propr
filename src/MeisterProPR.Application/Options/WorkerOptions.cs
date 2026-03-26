using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configuration options for the review job background worker.
///     Bound from environment variables; validated on application startup.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>
    ///     Minutes before a job stuck in the <c>Processing</c> state is transitioned to <c>Failed</c>.
    ///     Bound to <c>WORKER_STUCK_JOB_TIMEOUT_MINUTES</c>.
    /// </summary>
    [Range(5, 1440, ErrorMessage = "StuckJobTimeoutMinutes must be between 5 and 1440.")]
    public int StuckJobTimeoutMinutes { get; set; } = 30;
}
