using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Per-client (or per-crawl-config) full-text replacement for a named AI review prompt segment.
///     When the review pipeline assembles prompts it queries for the most-specific applicable override
///     and substitutes it for the corresponding hardcoded constant in <c>ReviewPrompts</c>.
/// </summary>
public sealed class PromptOverride
{
    /// <summary>Named prompt segments that may be overridden.</summary>
    public static readonly IReadOnlySet<string> ValidPromptKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "SystemPrompt",
        "AgenticLoopGuidance",
        "SynthesisSystemPrompt",
        "QualityFilterSystemPrompt",
        "PerFileContextPrompt",
    };

    /// <summary>Creates a new <see cref="PromptOverride" />.</summary>
    public PromptOverride(
        Guid id,
        Guid clientId,
        Guid? crawlConfigId,
        PromptOverrideScope scope,
        string promptKey,
        string overrideText)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        if (scope == PromptOverrideScope.CrawlConfigScope && crawlConfigId is null)
        {
            throw new ArgumentException("CrawlConfigId is required when scope is CrawlConfigScope.", nameof(crawlConfigId));
        }

        if (scope == PromptOverrideScope.ClientScope && crawlConfigId is not null)
        {
            throw new ArgumentException("CrawlConfigId must be null when scope is ClientScope.", nameof(crawlConfigId));
        }

        if (string.IsNullOrWhiteSpace(promptKey) || !ValidPromptKeys.Contains(promptKey))
        {
            throw new ArgumentException(
                $"PromptKey must be one of: {string.Join(", ", ValidPromptKeys)}.",
                nameof(promptKey));
        }

        if (string.IsNullOrWhiteSpace(overrideText))
        {
            throw new ArgumentException("OverrideText required.", nameof(overrideText));
        }

        this.Id = id;
        this.ClientId = clientId;
        this.CrawlConfigId = crawlConfigId;
        this.Scope = scope;
        this.PromptKey = promptKey;
        this.OverrideText = overrideText;
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning client.</summary>
    public Guid ClientId { get; private set; }

    /// <summary>Crawl configuration this override is scoped to. Null when <see cref="Scope" /> is <see cref="PromptOverrideScope.ClientScope" />.</summary>
    public Guid? CrawlConfigId { get; private set; }

    /// <summary>Whether this override is client-scoped or crawl-config-scoped.</summary>
    public PromptOverrideScope Scope { get; private set; }

    /// <summary>
    ///     Named prompt segment this override replaces.
    ///     Valid values: SystemPrompt, AgenticLoopGuidance, SynthesisSystemPrompt, QualityFilterSystemPrompt, PerFileContextPrompt.
    /// </summary>
    public string PromptKey { get; private set; }

    /// <summary>Full replacement text for the prompt segment.</summary>
    public string OverrideText { get; private set; }

    /// <summary>When this override was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When this override was last modified (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Replaces the override text and updates the modification timestamp.</summary>
    public void UpdateText(string overrideText)
    {
        if (string.IsNullOrWhiteSpace(overrideText))
        {
            throw new ArgumentException("OverrideText required.", nameof(overrideText));
        }

        this.OverrideText = overrideText;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
