namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Status of a mention reply job.
/// </summary>
public enum MentionJobStatus
{
    /// <summary>Job is queued and waiting to start.</summary>
    Pending,

    /// <summary>Job is currently processing.</summary>
    Processing,

    /// <summary>Job completed successfully.</summary>
    Completed,

    /// <summary>Job failed with an error.</summary>
    Failed,
}
