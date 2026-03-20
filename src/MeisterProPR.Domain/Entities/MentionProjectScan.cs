namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Tracks the project-level watermark for mention scanning.
///     One row per <c>CrawlConfiguration</c>. The <see cref="LastScannedAt" /> value
///     is used as <c>minLastUpdateDate</c> in ADO PR list queries.
/// </summary>
public sealed class MentionProjectScan
{
    /// <summary>
    ///     Creates a new <see cref="MentionProjectScan" />.
    /// </summary>
    public MentionProjectScan(Guid id, Guid crawlConfigurationId, DateTimeOffset lastScannedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (crawlConfigurationId == Guid.Empty)
        {
            throw new ArgumentException("CrawlConfigurationId must not be empty.", nameof(crawlConfigurationId));
        }

        this.Id = id;
        this.CrawlConfigurationId = crawlConfigurationId;
        this.LastScannedAt = lastScannedAt;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>FK to the crawl configuration this watermark belongs to.</summary>
    public Guid CrawlConfigurationId { get; init; }

    /// <summary>
    ///     Latest ADO PR last-update time observed; passed as <c>minLastUpdateDate</c>
    ///     on the next scan cycle.
    /// </summary>
    public DateTimeOffset LastScannedAt { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
