namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Per-client suppression pattern for repeated AI reviewer findings.
///     When an admin dismisses a recurring finding, the <see cref="PatternText" /> is injected
///     into the AI system prompt as an exclusion rule on all future reviews for that client.
/// </summary>
public sealed class FindingDismissal
{
    /// <summary>Creates a new <see cref="FindingDismissal" />.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="clientId">Client that owns this dismissal.</param>
    /// <param name="patternText">
    ///     Normalized pattern text (lowercase, max 200 chars) derived from the original finding message.
    ///     Injected into the AI system prompt as an exclusion entry.
    /// </param>
    /// <param name="label">Optional admin-provided label, e.g. "Accepted pattern — deliberate nullable usage".</param>
    /// <param name="originalMessage">The original full finding message, preserved for admin review UI.</param>
    public FindingDismissal(Guid id, Guid clientId, string patternText, string? label, string originalMessage)
    {
        this.Id = id;
        this.ClientId = clientId;
        this.PatternText = patternText;
        this.Label = label;
        this.OriginalMessage = originalMessage;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>Client that owns this dismissal.</summary>
    public Guid ClientId { get; private set; }

    /// <summary>
    ///     Normalized pattern text derived from the original finding message.
    ///     Lowercase, punctuation stripped, max 200 characters.
    ///     Injected into the AI system prompt as an exclusion entry.
    /// </summary>
    public string PatternText { get; private set; }

    /// <summary>Optional admin-provided label, e.g. "Accepted pattern — deliberate nullable usage".</summary>
    public string? Label { get; private set; }

    /// <summary>The original full finding message, preserved for admin review UI.</summary>
    public string OriginalMessage { get; private set; }

    /// <summary>When this dismissal was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Updates the admin label on this dismissal.</summary>
    /// <param name="label">New label, or <see langword="null" /> to clear it.</param>
    public void UpdateLabel(string? label) => this.Label = label;
}
