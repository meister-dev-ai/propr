// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configuration options for the agentic AI review loop. Validated on application startup.
/// </summary>
/// <remarks>
///     Only options whose summary names an <c>AI_*</c> environment variable are overridable at runtime
///     (wired in <c>InfrastructureServiceExtensions.ConfigureAiReviewOptions</c>). The remaining options are
///     fixed tuning defaults with no configuration binding.
/// </remarks>
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
    public int MaxFileReviewConcurrency { get; set; } = 3;

    /// <summary>
    ///     Maximum number of retries for a review job with failed file passes.
    ///     Bound to <c>AI_MAX_FILE_REVIEW_RETRIES</c>.
    /// </summary>
    [Range(1, 10, ErrorMessage = "MaxFileReviewRetries must be between 1 and 10.")]
    public int MaxFileReviewRetries { get; set; } = 3;

    /// <summary>
    ///     Maximum number of transparent retries for rate-limit (429) responses from the AI endpoint.
    ///     Bound to <c>AI_MAX_RATE_LIMIT_RETRIES</c>.
    /// </summary>
    [Range(1, 10, ErrorMessage = "MaxRateLimitRetries must be between 1 and 10.")]
    public int MaxRateLimitRetries { get; set; } = 3;

    /// <summary>
    ///     Maximum backoff delay in seconds between 429 retries.
    ///     Bound to <c>AI_MAX_BACKOFF_SECONDS</c>.
    /// </summary>
    [Range(5, 120, ErrorMessage = "MaxBackoffSeconds must be between 5 and 120.")]
    public int MaxBackoffSeconds { get; set; } = 30;

    /// <summary>
    ///     Fallback model identifier passed as <see cref="Microsoft.Extensions.AI.ChatOptions.ModelId" />
    ///     when a caller does not supply a client-scoped model explicitly.
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    ///     Maximum agentic loop iterations for <see cref="MeisterProPR.Domain.Enums.FileComplexityTier.Low" /> files.
    ///     Bound to <c>AI_MAX_ITERATIONS_LOW</c>.
    /// </summary>
    [Range(1, 100, ErrorMessage = "MaxIterationsLow must be between 1 and 100.")]
    public int MaxIterationsLow { get; set; } = 5;

    /// <summary>
    ///     Maximum agentic loop iterations for <see cref="MeisterProPR.Domain.Enums.FileComplexityTier.Medium" /> files.
    ///     Bound to <c>AI_MAX_ITERATIONS_MEDIUM</c>.
    /// </summary>
    [Range(1, 100, ErrorMessage = "MaxIterationsMedium must be between 1 and 100.")]
    public int MaxIterationsMedium { get; set; } = 10;

    /// <summary>
    ///     Maximum agentic loop iterations for <see cref="MeisterProPR.Domain.Enums.FileComplexityTier.High" /> files.
    ///     Bound to <c>AI_MAX_ITERATIONS_HIGH</c>.
    /// </summary>
    [Range(1, 100, ErrorMessage = "MaxIterationsHigh must be between 1 and 100.")]
    public int MaxIterationsHigh { get; set; } = 20;

    /// <summary>Maximum output tokens for low-complexity file reviews.</summary>
    [Range(512, 32768, ErrorMessage = "MaxOutputTokensLow must be between 512 and 32768.")]
    public int MaxOutputTokensLow { get; set; } = 4096;

    /// <summary>Maximum output tokens for medium-complexity file reviews.</summary>
    [Range(512, 32768, ErrorMessage = "MaxOutputTokensMedium must be between 512 and 32768.")]
    public int MaxOutputTokensMedium { get; set; } = 6144;

    /// <summary>Maximum output tokens for high-complexity file reviews and non-file review paths.</summary>
    [Range(512, 32768, ErrorMessage = "MaxOutputTokensHigh must be between 512 and 32768.")]
    public int MaxOutputTokensHigh { get; set; } = 8192;

    /// <summary>Maximum characters retained from one bulky tool result before replay summarisation metadata is recorded.</summary>
    [Range(1024, 200000, ErrorMessage = "MaxToolResultReplayCharacters must be between 1024 and 200000.")]
    public int MaxToolResultReplayCharacters { get; set; } = 32000;

    /// <summary>
    ///     EXPERIMENTAL / A-B ONLY. When <see langword="true" />, tool evidence (e.g. fetched file contents) is
    ///     retained across agentic-loop compaction and re-injected as a stable, deduplicated block so the model
    ///     does not re-fetch the same content on later iterations. This changes both the token profile AND the
    ///     review's convergence behaviour, so it must be validated on the evaluation harness before it ships;
    ///     the default keeps the existing drop-on-compaction behaviour. Bound to <c>AI_ENABLE_RETAINED_TOOL_EVIDENCE</c>.
    /// </summary>
    public bool EnableRetainedToolEvidence { get; set; } = false;

    /// <summary>
    ///     Maximum number of distinct tool-evidence entries retained across compaction when
    ///     <see cref="EnableRetainedToolEvidence" /> is enabled. Older entries beyond this count are not retained.
    /// </summary>
    [Range(1, 100, ErrorMessage = "MaxRetainedToolEvidenceEntries must be between 1 and 100.")]
    public int MaxRetainedToolEvidenceEntries { get; set; } = 16;

    /// <summary>
    ///     Maximum total character budget for the retained tool-evidence block when
    ///     <see cref="EnableRetainedToolEvidence" /> is enabled. Prevents the retained context from growing
    ///     without bound across iterations.
    /// </summary>
    [Range(1024, 400000, ErrorMessage = "MaxRetainedToolEvidenceChars must be between 1024 and 400000.")]
    public int MaxRetainedToolEvidenceChars { get; set; } = 48000;

    /// <summary>
    ///     When <see langword="true" /> (the default), the model's reasoning content (<c>TextReasoningContent</c>) is
    ///     captured into the recorded assistant-turn output for diagnostics. Reasoning can contain verbatim source
    ///     excerpts, so set this to <see langword="false" /> where data-retention policy requires it; assistant text
    ///     and tool calls are always recorded regardless. Bound to <c>AI_CAPTURE_REASONING_IN_PROTOCOL</c>.
    /// </summary>
    public bool CaptureReasoningInProtocol { get; set; } = true;

    /// <summary>
    ///     Maximum number of characters of captured reasoning retained per assistant turn (applied at write time,
    ///     before persistence). Prevents unbounded reasoning text from bloating the recorded protocol; only consulted
    ///     when <see cref="CaptureReasoningInProtocol" /> is enabled.
    /// </summary>
    [Range(256, 100000, ErrorMessage = "MaxReasoningSummaryChars must be between 256 and 100000.")]
    public int MaxReasoningSummaryChars { get; set; } = 4000;

    /// <summary>
    ///     Minimum confidence score (0–100) required to post a comment at ERROR severity.
    ///     Comments below this threshold are automatically downgraded to WARNING before posting.
    ///     Bound to <c>AI_CONFIDENCE_FLOOR_ERROR</c>.
    /// </summary>
    [Range(0, 100, ErrorMessage = "ConfidenceFloorError must be between 0 and 100.")]
    public int ConfidenceFloorError { get; set; } = 80;

    /// <summary>
    ///     Minimum confidence score (0–100) required to post a comment at WARNING severity.
    ///     Comments below this threshold are automatically downgraded to SUGGESTION before posting.
    ///     Bound to <c>AI_CONFIDENCE_FLOOR_WARNING</c>.
    /// </summary>
    [Range(0, 100, ErrorMessage = "ConfidenceFloorWarning must be between 0 and 100.")]
    public int ConfidenceFloorWarning { get; set; } = 60;

    /// <summary>
    ///     Minimum cosine similarity (0–1) to a hedged/vague exemplar centroid for the semantic comment screener
    ///     to classify a comment as hedged or vague; below this the comment is kept as firm. Only consulted on the
    ///     language-robust screening path (<c>EnableLanguageRobustScreening</c>). The default (0.40) was chosen from
    ///     a de/fr/it validation sweep: firm comments were preserved 100% down to 0.30, while 0.50 caught only ~half
    ///     of non-English hedge/vague; 0.40 recovers most of that recall while keeping a margin against demoting
    ///     borderline-firm comments on harder inputs.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "CommentScreeningSimilarityThreshold must be between 0.0 and 1.0.")]
    public double CommentScreeningSimilarityThreshold { get; set; } = 0.40;

    /// <summary>
    ///     Minimum total comment count across all files before the cross-file quality filter AI pass
    ///     is invoked. Below this threshold, comments are posted as-is after per-file filtering.
    ///     Bound to <c>AI_QUALITY_FILTER_THRESHOLD</c>.
    /// </summary>
    [Range(1, 500, ErrorMessage = "QualityFilterThreshold must be between 1 and 500.")]
    public int QualityFilterThreshold { get; set; } = 20;

    /// <summary>Maximum number of candidate findings kept after per-file importance ranking.</summary>
    [Range(1, 100, ErrorMessage = "ImportanceRankingKeepTopN must be between 1 and 100.")]
    public int ImportanceRankingKeepTopN { get; set; } = 8;

    /// <summary>Minimum 1-10 importance score required for a ranked finding to survive.</summary>
    [Range(1, 10, ErrorMessage = "ImportanceRankingMinScore must be between 1 and 10.")]
    public int ImportanceRankingMinScore { get; set; } = 4;

    /// <summary>Maximum number of cross-file caller sites to prefetch into the prompt evidence channel.</summary>
    [Range(0, 20, ErrorMessage = "MaxPrefetchCallerSites must be between 0 and 20.")]
    public int MaxPrefetchCallerSites { get; set; } = 5;

    /// <summary>Maximum number of characters injected from prefetched surrounding-code regions.</summary>
    [Range(256, 32000, ErrorMessage = "MaxPrefetchRegionChars must be between 256 and 32000.")]
    public int MaxPrefetchRegionChars { get; set; } = 4000;

    /// <summary>
    ///     Number of lines before each changed hunk to include in the hunk-centered prefetch window.
    ///     Asymmetrically larger than <see cref="PrefetchWindowLinesAfter" /> because code before a change
    ///     (signatures, setup) matters more for context than code after. Fixed tuning default; not
    ///     environment-overridable.
    /// </summary>
    [Range(0, 500, ErrorMessage = "PrefetchWindowLinesBefore must be between 0 and 500.")]
    public int PrefetchWindowLinesBefore { get; set; } = 40;

    /// <summary>
    ///     Number of lines after each changed hunk to include in the hunk-centered prefetch window.
    ///     Fixed tuning default; not environment-overridable.
    /// </summary>
    [Range(0, 500, ErrorMessage = "PrefetchWindowLinesAfter must be between 0 and 500.")]
    public int PrefetchWindowLinesAfter { get; set; } = 15;

    /// <summary>
    ///     Maximum number of past resolutions to retrieve per file review query.
    ///     Bound to <c>AI_MEMORY_TOP_N</c>. Range: 1–20.
    /// </summary>
    [Range(1, 20, ErrorMessage = "MemoryTopN must be between 1 and 20.")]
    public int MemoryTopN { get; set; } = 3;

    /// <summary>
    ///     Minimum cosine similarity score (0.0–1.0) for a past resolution to be included in
    ///     reconsideration context. Bound to <c>AI_MEMORY_MIN_SIMILARITY</c>.
    /// </summary>
    [Range(0.0f, 1.0f, ErrorMessage = "MemoryMinSimilarity must be between 0.0 and 1.0.")]
    public float MemoryMinSimilarity { get; set; } = 0.80f;

    /// <summary>
    ///     Embedding vector dimensions; must match the configured embedding model.
    ///     Bound to <c>AI_MEMORY_EMBEDDING_DIMENSIONS</c>. Range: 64–4096.
    /// </summary>
    [Range(64, 4096, ErrorMessage = "MemoryEmbeddingDimensions must be between 64 and 4096.")]
    public int MemoryEmbeddingDimensions { get; set; } = 1536;

    /// <summary>
    ///     Kill-switch for the internal Tree-sitter structural boundary resolver (feature 070).
    ///     When <c>false</c>, the prefetch stage uses the existing heuristic everywhere and
    ///     the analyzer is not consulted. Bound to <c>AI_ENABLE_STRUCTURAL_BOUNDARY_RESOLUTION</c>.
    /// </summary>
    public bool EnableStructuralBoundaryResolution { get; set; } = true;

    /// <summary>
    ///     Per-parse wall-clock timeout for the structural analyzer (R8 step 2), in milliseconds.
    ///     On overshoot the analyzer returns empty with <see cref="FallbackReason.ParseTimeout" /> and
    ///     the prefetch falls back to the heuristic. Bound to <c>AI_STRUCTURAL_PARSE_TIMEOUT_MS</c>.
    /// </summary>
    [Range(10, 5000, ErrorMessage = "StructuralParseTimeoutMs must be between 10 and 5000.")]
    public int StructuralParseTimeoutMs { get; set; } = 200;

    /// <summary>
    ///     Maximum source size, in bytes, the structural analyzer will accept (R8 step 1).
    ///     Files larger than this skip parsing with <see cref="FallbackReason.FileTooLarge" />.
    ///     Bound to <c>AI_MAX_STRUCTURAL_PARSE_BYTES</c>.
    /// </summary>
    [Range(1024, 5_242_880, ErrorMessage = "MaxStructuralParseBytes must be between 1024 and 5242880.")]
    public int MaxStructuralParseBytes { get; set; } = 524_288;

    /// <summary>
    ///     Kill-switch for the cross-file structural reference surface: the
    ///     <c>find_references</c>/<c>get_definition</c> tools, the <c>related_symbol</c> structural
    ///     confirmation, and the deterministic caller-evidence feed. When <c>false</c>, the tools are
    ///     not registered, <c>related_symbol</c> keeps today's substring behavior, and no caller
    ///     evidence is injected. Bound to <c>AI_ENABLE_STRUCTURAL_REFERENCE_TOOLS</c>.
    /// </summary>
    public bool EnableStructuralReferenceTools { get; set; } = true;

    /// <summary>
    ///     Maximum number of candidate files scanned per reference/definition lookup before the
    ///     result is marked truncated. Bound to <c>AI_MAX_REFERENCE_CANDIDATE_FILES</c>.
    /// </summary>
    [Range(1, 2000, ErrorMessage = "MaxReferenceCandidateFiles must be between 1 and 2000.")]
    public int MaxReferenceCandidateFiles { get; set; } = 200;

    /// <summary>
    ///     Maximum number of confirmed reference/definition sites returned per lookup before the
    ///     result is marked truncated. Bound to <c>AI_MAX_REFERENCE_RESULTS</c>.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "MaxReferenceResults must be between 1 and 1000.")]
    public int MaxReferenceResults { get; set; } = 50;

    /// <summary>
    ///     Maximum total character budget for a serialized reference/definition tool result before the
    ///     result is marked truncated. Bound to <c>AI_MAX_REFERENCE_RESULT_CHARS</c>.
    /// </summary>
    [Range(256, 64000, ErrorMessage = "MaxReferenceResultChars must be between 256 and 64000.")]
    public int MaxReferenceResultChars { get; set; } = 8000;

    /// <summary>
    ///     Per-operation wall-clock budget for a reference/definition lookup (across all candidate
    ///     files), in milliseconds. On overshoot the lookup returns what it has, marked truncated.
    ///     Bound to <c>AI_REFERENCE_RESOLUTION_TIMEOUT_MS</c>.
    /// </summary>
    [Range(50, 30000, ErrorMessage = "ReferenceResolutionTimeoutMs must be between 50 and 30000.")]
    public int ReferenceResolutionTimeoutMs { get; set; } = 4000;

    /// <summary>
    ///     Maximum number of linked work items / issues injected into the eager review context before the
    ///     rest are dropped (and the drop logged). Bound to <c>AI_MAX_LINKED_ITEMS_IN_CONTEXT</c>.
    /// </summary>
    [Range(1, 50, ErrorMessage = "MaxLinkedItemsInContext must be between 1 and 50.")]
    public int MaxLinkedItemsInContext { get; set; } = 5;

    /// <summary>
    ///     Maximum character length each linked item's description is truncated to for the eager context.
    ///     Bound to <c>AI_MAX_LINKED_ITEM_DESCRIPTION_CHARS</c>.
    /// </summary>
    [Range(128, 20000, ErrorMessage = "MaxLinkedItemDescriptionChars must be between 128 and 20000.")]
    public int MaxLinkedItemDescriptionChars { get; set; } = 2000;

    /// <summary>
    ///     Kill-switch for the on-demand linked-item review tools (<c>get_linked_item_details</c>,
    ///     <c>get_linked_item_discussion</c>, <c>resolve_linked_item</c>). When <c>false</c> the tools are
    ///     not registered even for clients that include linked items. Bound to <c>AI_ENABLE_LINKED_ITEM_TOOLS</c>.
    /// </summary>
    public bool EnableLinkedItemTools { get; set; } = true;

    /// <summary>
    ///     Maximum number of on-demand linked-item tool calls allowed per review before further calls are
    ///     blocked (budget-exhausted). Bound to <c>AI_MAX_LINKED_ITEM_TOOL_CALLS</c>.
    /// </summary>
    [Range(0, 100, ErrorMessage = "MaxLinkedItemToolCalls must be between 0 and 100.")]
    public int MaxLinkedItemToolCalls { get; set; } = 6;

    /// <summary>
    ///     Maximum character budget for a single serialized linked-item tool result before it is truncated.
    ///     Bound to <c>AI_MAX_LINKED_ITEM_TOOL_RESULT_CHARS</c>.
    /// </summary>
    [Range(256, 64000, ErrorMessage = "MaxLinkedItemToolResultChars must be between 256 and 64000.")]
    public int MaxLinkedItemToolResultChars { get; set; } = 8000;

    /// <summary>
    ///     Per-call wall-clock budget for a single on-demand linked-item tool lookup, in milliseconds. On
    ///     overshoot the call returns empty (fail-soft). Bound to <c>AI_LINKED_ITEM_TOOL_TIMEOUT_MS</c>.
    /// </summary>
    [Range(100, 30000, ErrorMessage = "LinkedItemToolTimeoutMs must be between 100 and 30000.")]
    public int LinkedItemToolTimeoutMs { get; set; } = 5000;
}
