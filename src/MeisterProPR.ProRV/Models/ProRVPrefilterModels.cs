// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.ProRV.Models;

/// <summary>
///     Input for ProRV diff-based relevance prefiltering.
/// </summary>
public sealed class ProRVPrefilterRequest
{
    /// <summary>
    ///     Creates a new prefilter request.
    /// </summary>
    /// <param name="filePath">Relative path of the changed file.</param>
    /// <param name="unifiedDiff">Unified diff content for the changed file.</param>
    public ProRVPrefilterRequest(string filePath, string unifiedDiff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        this.FilePath = filePath;
        this.UnifiedDiff = unifiedDiff ?? string.Empty;
    }

    /// <summary>
    ///     Gets the relative path of the changed file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    ///     Gets the unified diff content for the changed file.
    /// </summary>
    public string UnifiedDiff { get; }

    /// <summary>
    ///     Gets or sets the explicit language identifier to use. When omitted, ProRV attempts to infer
    ///     a language from file path and technology hints.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    ///     Gets or sets optional technology or framework hints such as <c>dotnet</c>, <c>aspnet</c>,
    ///     or <c>msbuild</c>.
    /// </summary>
    public IReadOnlyList<string> TechnologyHints { get; init; } = [];

    /// <summary>
    ///     Gets or sets the maximum number of ranked items to return.
    /// </summary>
    public int MaxResults { get; init; } = 8;
}

/// <summary>
///     High-level outcome of the ProRV prefilter stage.
/// </summary>
public enum ProRVPrefilterStatus
{
    /// <summary>
    ///     ProRV returned a ranked set successfully.
    /// </summary>
    Success,

    /// <summary>
    ///     The request language could not be resolved to a supported ProRV asset set.
    /// </summary>
    UnsupportedLanguage,

    /// <summary>
    ///     No embedded checks were available for the resolved language.
    /// </summary>
    EmptyCatalog,

    /// <summary>
    ///     The prefilter model returned content that could not be parsed into the expected JSON schema.
    /// </summary>
    UnparseableResponse,
}

/// <summary>
///     Ranked prefilter result for one changed file.
/// </summary>
public sealed class ProRVPrefilterResult
{
    /// <summary>
    ///     Creates a new prefilter result.
    /// </summary>
    public ProRVPrefilterResult(
        ProRVPrefilterStatus status,
        string filePath,
        string? language,
        IReadOnlyList<ProRVRelevantItem> items,
        string? rawResponse = null,
        string? failureReason = null,
        long? inputTokens = null,
        long? outputTokens = null)
    {
        this.Status = status;
        this.FilePath = filePath;
        this.Language = language;
        this.Items = items;
        this.RawResponse = rawResponse;
        this.FailureReason = failureReason;
        this.InputTokens = inputTokens;
        this.OutputTokens = outputTokens;
    }

    /// <summary>
    ///     Gets the prefilter status.
    /// </summary>
    public ProRVPrefilterStatus Status { get; }

    /// <summary>
    ///     Gets the changed file path that was evaluated.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    ///     Gets the resolved language used for the ranking request, if any.
    /// </summary>
    public string? Language { get; }

    /// <summary>
    ///     Gets the ranked relevant items.
    /// </summary>
    public IReadOnlyList<ProRVRelevantItem> Items { get; }

    /// <summary>
    ///     Gets the raw model response when available.
    /// </summary>
    public string? RawResponse { get; }

    /// <summary>
    ///     Gets a machine-readable or human-readable failure explanation when the status is not success.
    /// </summary>
    public string? FailureReason { get; }

    /// <summary>
    ///     Gets the provider-reported input token count for the prefilter call when available.
    /// </summary>
    public long? InputTokens { get; }

    /// <summary>
    ///     Gets the provider-reported output token count for the prefilter call when available.
    /// </summary>
    public long? OutputTokens { get; }
}

/// <summary>
///     One ranked ProRV candidate returned by the prefilter stage.
/// </summary>
public sealed class ProRVRelevantItem
{
    /// <summary>
    ///     Creates a new relevant item.
    /// </summary>
    public ProRVRelevantItem(
        string id,
        string title,
        string shortDescription,
        string instruction,
        string reason,
        int score,
        string severity,
        string precision,
        IReadOnlyList<string> tags)
    {
        this.Id = id;
        this.Title = title;
        this.ShortDescription = shortDescription;
        this.Instruction = instruction;
        this.Reason = reason;
        this.Score = score;
        this.Severity = severity;
        this.Precision = precision;
        this.Tags = tags;
    }

    /// <summary>
    ///     Gets the stable ProRV item identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    ///     Gets the human-readable item title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    ///     Gets the compact description used in the prefilter index.
    /// </summary>
    public string ShortDescription { get; }

    /// <summary>
    ///     Gets the fuller instruction text that can be used for the next refinement step.
    /// </summary>
    public string Instruction { get; }

    /// <summary>
    ///     Gets the short rationale produced by the prefilter model.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    ///     Gets the relative ranking score from 0 to 100.
    /// </summary>
    public int Score { get; }

    /// <summary>
    ///     Gets the upstream-derived severity label.
    /// </summary>
    public string Severity { get; }

    /// <summary>
    ///     Gets the upstream-derived precision label.
    /// </summary>
    public string Precision { get; }

    /// <summary>
    ///     Gets the associated tags from the embedded knowledge index.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }
}
