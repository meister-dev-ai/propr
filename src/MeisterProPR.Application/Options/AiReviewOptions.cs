using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configuration options for the agentic AI review loop.
///     Bound from environment variables; validated on application startup.
/// </summary>
public sealed class AiReviewOptions
{
    /// <summary>Maximum number of agentic loop iterations per review. Bound to <c>AI_MAX_REVIEW_ITERATIONS</c>.</summary>
    [Range(1, 100, ErrorMessage = "MaxIterations must be between 1 and 100.")]
    public int MaxIterations { get; set; } = 20;

    /// <summary>Number of lines returned per <c>get_file_content</c> tool call. Bound to <c>AI_FILE_BATCH_LINES</c>.</summary>
    [Range(10, 1000, ErrorMessage = "FileBatchLines must be between 10 and 1000.")]
    public int FileBatchLines { get; set; } = 100;

    /// <summary>
    ///     Confidence score threshold (0–100) at which the review loop stops investigating a concern.
    ///     Bound to <c>AI_CONFIDENCE_THRESHOLD</c>.
    /// </summary>
    [Range(0, 100, ErrorMessage = "ConfidenceThreshold must be between 0 and 100.")]
    public int ConfidenceThreshold { get; set; } = 70;

    /// <summary>
    ///     Maximum file size in bytes for the <c>get_file_content</c> tool. Files exceeding this limit return an
    ///     error string instead of content. Bound to <c>AI_MAX_FILE_SIZE_BYTES</c>.
    /// </summary>
    [Range(1024, int.MaxValue, ErrorMessage = "MaxFileSizeBytes must be at least 1024.")]
    public int MaxFileSizeBytes { get; set; } = 1_048_576;

    /// <summary>
    ///     Maximum number of per-file review passes to run in parallel.
    ///     Bound to <c>AI_MAX_FILE_REVIEW_CONCURRENCY</c>.
    /// </summary>
    [Range(1, 10, ErrorMessage = "MaxFileReviewConcurrency must be between 1 and 10.")]
    public int MaxFileReviewConcurrency { get; set; } = 2;

    /// <summary>
    ///     Maximum number of retries for a review job with failed file passes.
    ///     Bound to <c>AI_MAX_FILE_REVIEW_RETRIES</c>.
    /// </summary>
    [Range(1, 10, ErrorMessage = "MaxFileReviewRetries must be between 1 and 10.")]
    public int MaxFileReviewRetries { get; set; } = 3;
}
