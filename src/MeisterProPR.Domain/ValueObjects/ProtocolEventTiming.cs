// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Canonical timing availability states used by protocol tool-call diagnostics.
/// </summary>
public static class ProtocolEventTimingAvailabilities
{
    public const string Captured = "captured";
    public const string NotCaptured = "not_captured";
    public const string Unavailable = "unavailable";
    public const string Incomplete = "incomplete";
}

/// <summary>
///     Canonical tool-call and phase outcomes used by protocol diagnostics.
/// </summary>
public static class ProtocolEventToolOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Incomplete = "incomplete";
    public const string Skipped = "skipped";
}

/// <summary>
///     Stable phase keys used by repository-search-related timing segments.
/// </summary>
public static class ProtocolEventToolPhaseNames
{
    public const string RequestPreparation = "request_preparation";
    public const string ProviderApiCall = "provider_api_call";
    public const string ScmFileTreeFetch = "scm_file_tree_fetch";
    public const string ScmFileContentFetch = "scm_file_content_fetch";
    public const string RepositorySearch = "repository_search";
    public const string RetryBackoff = "retry_backoff";
    public const string ResultShaping = "result_shaping";
    public const string Summarization = "summarization";
    public const string Bounding = "bounding";
}

/// <summary>
///     One ordered timed phase captured inside a tool invocation.
/// </summary>
public sealed record ProtocolEventPhaseTiming(
    string Name,
    string DisplayName,
    int Sequence,
    int? Occurrence,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMs,
    string Availability,
    string Outcome,
    string? Summary = null);
