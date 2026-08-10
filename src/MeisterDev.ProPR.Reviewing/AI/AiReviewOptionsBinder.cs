// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Binds the review options from environment variables.
///     <para>
///         Shared by every host that runs a review, because the values decide how the pipeline behaves —
///         how many iterations a file gets, what a finding must clear to be kept, which tools exist. Two
///         hosts binding them separately would drift, and a review would mean one thing in the control
///         plane and another on a runner without either side saying so.
///     </para>
/// </summary>
public static class AiReviewOptionsBinder
{
    /// <summary>Applies the configured values over the defaults already on <paramref name="opts" />.</summary>
    /// <param name="opts">The options to fill.</param>
    /// <param name="configuration">Where the values are read from.</param>
    public static void Bind(AiReviewOptions opts, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(configuration);

        opts.MaxIterations = TryGetInt(configuration, "AI_MAX_REVIEW_ITERATIONS") ?? opts.MaxIterations;
        opts.FileBatchLines = TryGetInt(configuration, "AI_FILE_BATCH_LINES") ?? opts.FileBatchLines;
        opts.ConfidenceThreshold = TryGetInt(configuration, "AI_CONFIDENCE_THRESHOLD") ?? opts.ConfidenceThreshold;
        opts.MaxFileSizeBytes = TryGetInt(configuration, "AI_MAX_FILE_SIZE_BYTES") ?? opts.MaxFileSizeBytes;
        opts.MaxFileReviewConcurrency = TryGetInt(configuration, "AI_MAX_FILE_REVIEW_CONCURRENCY") ?? opts.MaxFileReviewConcurrency;
        opts.MaxFileReviewRetries = TryGetInt(configuration, "AI_MAX_FILE_REVIEW_RETRIES") ?? opts.MaxFileReviewRetries;
        opts.MaxRateLimitRetries = TryGetInt(configuration, "AI_MAX_RATE_LIMIT_RETRIES") ?? opts.MaxRateLimitRetries;
        opts.MaxBackoffSeconds = TryGetInt(configuration, "AI_MAX_BACKOFF_SECONDS") ?? opts.MaxBackoffSeconds;
        opts.MaxIterationsLow = TryGetInt(configuration, "AI_MAX_ITERATIONS_LOW") ?? opts.MaxIterationsLow;
        opts.MaxIterationsMedium = TryGetInt(configuration, "AI_MAX_ITERATIONS_MEDIUM") ?? opts.MaxIterationsMedium;
        opts.MaxIterationsHigh = TryGetInt(configuration, "AI_MAX_ITERATIONS_HIGH") ?? opts.MaxIterationsHigh;
        opts.ConfidenceFloorError = TryGetInt(configuration, "AI_CONFIDENCE_FLOOR_ERROR") ?? opts.ConfidenceFloorError;
        opts.ConfidenceFloorWarning = TryGetInt(configuration, "AI_CONFIDENCE_FLOOR_WARNING") ?? opts.ConfidenceFloorWarning;
        opts.QualityFilterThreshold = TryGetInt(configuration, "AI_QUALITY_FILTER_THRESHOLD") ?? opts.QualityFilterThreshold;
        opts.MemoryTopN = TryGetInt(configuration, "AI_MEMORY_TOP_N") ?? opts.MemoryTopN;
        opts.MemoryMinSimilarity = TryGetFloat(configuration, "AI_MEMORY_MIN_SIMILARITY") ?? opts.MemoryMinSimilarity;
        opts.MemoryEmbeddingDimensions = TryGetInt(configuration, "AI_MEMORY_EMBEDDING_DIMENSIONS") ?? opts.MemoryEmbeddingDimensions;
        opts.PostedFindingMinSimilarity =
            TryGetFloat(configuration, "AI_POSTED_FINDING_MIN_SIMILARITY") ?? opts.PostedFindingMinSimilarity;

        // Structural boundary resolution (feature 070).
        opts.EnableStructuralBoundaryResolution =
            TryGetBool(configuration, "AI_ENABLE_STRUCTURAL_BOUNDARY_RESOLUTION") ?? opts.EnableStructuralBoundaryResolution;
        opts.StructuralParseTimeoutMs = TryGetInt(configuration, "AI_STRUCTURAL_PARSE_TIMEOUT_MS") ?? opts.StructuralParseTimeoutMs;
        opts.MaxStructuralParseBytes = TryGetInt(configuration, "AI_MAX_STRUCTURAL_PARSE_BYTES") ?? opts.MaxStructuralParseBytes;

        // Cross-file structural reference surface.
        opts.EnableStructuralReferenceTools =
            TryGetBool(configuration, "AI_ENABLE_STRUCTURAL_REFERENCE_TOOLS") ?? opts.EnableStructuralReferenceTools;
        opts.MaxReferenceCandidateFiles = TryGetInt(configuration, "AI_MAX_REFERENCE_CANDIDATE_FILES") ?? opts.MaxReferenceCandidateFiles;
        opts.MaxReferenceResults = TryGetInt(configuration, "AI_MAX_REFERENCE_RESULTS") ?? opts.MaxReferenceResults;
        opts.MaxReferenceResultChars = TryGetInt(configuration, "AI_MAX_REFERENCE_RESULT_CHARS") ?? opts.MaxReferenceResultChars;
        opts.ReferenceResolutionTimeoutMs = TryGetInt(configuration, "AI_REFERENCE_RESOLUTION_TIMEOUT_MS") ?? opts.ReferenceResolutionTimeoutMs;

        // Cross-compaction tool-evidence retention (experimental; A/B only).
        opts.EnableRetainedToolEvidence =
            TryGetBool(configuration, "AI_ENABLE_RETAINED_TOOL_EVIDENCE") ?? opts.EnableRetainedToolEvidence;

        // Reasoning capture into recorded assistant-turn output (off by default; data-retention gate).
        opts.CaptureReasoningInProtocol =
            TryGetBool(configuration, "AI_CAPTURE_REASONING_IN_PROTOCOL") ?? opts.CaptureReasoningInProtocol;

        // Linked work items / issues in the review context.
        opts.MaxLinkedItemsInContext = TryGetInt(configuration, "AI_MAX_LINKED_ITEMS_IN_CONTEXT") ?? opts.MaxLinkedItemsInContext;
        opts.MaxLinkedItemDescriptionChars = TryGetInt(configuration, "AI_MAX_LINKED_ITEM_DESCRIPTION_CHARS") ?? opts.MaxLinkedItemDescriptionChars;
        opts.EnableLinkedItemTools = TryGetBool(configuration, "AI_ENABLE_LINKED_ITEM_TOOLS") ?? opts.EnableLinkedItemTools;
        opts.MaxLinkedItemToolCalls = TryGetInt(configuration, "AI_MAX_LINKED_ITEM_TOOL_CALLS") ?? opts.MaxLinkedItemToolCalls;
        opts.MaxLinkedItemToolResultChars = TryGetInt(configuration, "AI_MAX_LINKED_ITEM_TOOL_RESULT_CHARS") ?? opts.MaxLinkedItemToolResultChars;
        opts.LinkedItemToolTimeoutMs = TryGetInt(configuration, "AI_LINKED_ITEM_TOOL_TIMEOUT_MS") ?? opts.LinkedItemToolTimeoutMs;
    }

    private static int? TryGetInt(IConfiguration configuration, string key)
    {
        return int.TryParse(configuration[key], out var value) ? value : null;
    }

    private static float? TryGetFloat(IConfiguration configuration, string key)
    {
        return float.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static bool? TryGetBool(IConfiguration configuration, string key)
    {
        return bool.TryParse(configuration[key], out var value) ? value : null;
    }
}
