// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configuration options for the ProCursor bounded module.
///     Bound from <c>PROCURSOR_*</c> environment variables and validated on startup.
/// </summary>
public sealed class ProCursorOptions
{
    /// <summary>Maximum number of concurrent durable indexing jobs. Bound to <c>PROCURSOR_MAX_INDEX_CONCURRENCY</c>.</summary>
    [Range(1, 32, ErrorMessage = "MaxIndexConcurrency must be between 1 and 32.")]
    public int MaxIndexConcurrency { get; set; } = 2;

    /// <summary>
    ///     Maximum number of knowledge or symbol results returned to callers. Bound to <c>PROCURSOR_MAX_QUERY_RESULTS</c>
    ///     .
    /// </summary>
    [Range(1, 20, ErrorMessage = "MaxQueryResults must be between 1 and 20.")]
    public int MaxQueryResults { get; set; } = 5;

    /// <summary>Maximum number of eligible sources scanned for one query. Bound to <c>PROCURSOR_MAX_SOURCES_PER_QUERY</c>.</summary>
    [Range(1, 50, ErrorMessage = "MaxSourcesPerQuery must be between 1 and 50.")]
    public int MaxSourcesPerQuery { get; set; } = 20;

    /// <summary>Target chunk size, in lines, for indexed source text. Bound to <c>PROCURSOR_CHUNK_TARGET_LINES</c>.</summary>
    [Range(10, 1000, ErrorMessage = "ChunkTargetLines must be between 10 and 1000.")]
    public int ChunkTargetLines { get; set; } = 120;

    /// <summary>TTL, in minutes, for review mini-index overlays. Bound to <c>PROCURSOR_MINI_INDEX_TTL_MINUTES</c>.</summary>
    [Range(1, 1440, ErrorMessage = "MiniIndexTtlMinutes must be between 1 and 1440.")]
    public int MiniIndexTtlMinutes { get; set; } = 30;

    /// <summary>Polling interval, in seconds, for the durable index worker. Bound to <c>PROCURSOR_REFRESH_POLL_SECONDS</c>.</summary>
    [Range(1, 3600, ErrorMessage = "RefreshPollSeconds must be between 1 and 3600.")]
    public int RefreshPollSeconds { get; set; } = 30;

    /// <summary>
    ///     Retention window, in minutes, for stale temp workspaces before cleanup. Bound to
    ///     <c>PROCURSOR_TEMP_WORKSPACE_RETENTION_MINUTES</c>.
    /// </summary>
    [Range(1, 1440, ErrorMessage = "TempWorkspaceRetentionMinutes must be between 1 and 1440.")]
    public int TempWorkspaceRetentionMinutes { get; set; } = 120;

    /// <summary>Expected embedding vector width. Bound to <c>PROCURSOR_EMBEDDING_DIMENSIONS</c>.</summary>
    [Range(1, 4096, ErrorMessage = "EmbeddingDimensions must be between 1 and 4096.")]
    public int EmbeddingDimensions { get; set; } = 1536;
}
