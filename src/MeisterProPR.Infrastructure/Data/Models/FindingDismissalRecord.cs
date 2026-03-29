namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a per-client AI reviewer finding dismissal.</summary>
public sealed class FindingDismissalRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>varchar(200) — normalized pattern text used for AI prompt injection.</summary>
    public string PatternText { get; set; } = string.Empty;

    /// <summary>varchar(300), nullable — optional admin-provided label.</summary>
    public string? Label { get; set; }

    /// <summary>text — original full finding message, preserved for admin UI.</summary>
    public string OriginalMessage { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public ClientRecord? Client { get; set; }
}
