using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a per-client or per-crawl-config AI prompt override.</summary>
public sealed class PromptOverrideRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Nullable — only set when <see cref="Scope" /> is <see cref="PromptOverrideScope.CrawlConfigScope" />.</summary>
    public Guid? CrawlConfigId { get; set; }

    public PromptOverrideScope Scope { get; set; }

    /// <summary>varchar(100) — named prompt segment key.</summary>
    public string PromptKey { get; set; } = string.Empty;

    /// <summary>text — full replacement text for the prompt segment.</summary>
    public string OverrideText { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ClientRecord? Client { get; set; }

    public CrawlConfigurationRecord? CrawlConfig { get; set; }
}
