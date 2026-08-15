// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Tracks the project-level watermark for mention scanning.
///     One row per mention configuration. The <see cref="LastScannedAt" /> value
///     is used as <c>minLastUpdateDate</c> in ADO PR list queries.
/// </summary>
public sealed class MentionProjectScan
{
    /// <summary>
    ///     Creates a new <see cref="MentionProjectScan" />.
    /// </summary>
    public MentionProjectScan(Guid id, Guid mentionConfigurationId, DateTimeOffset lastScannedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (mentionConfigurationId == Guid.Empty)
        {
            throw new ArgumentException("MentionConfigurationId must not be empty.", nameof(mentionConfigurationId));
        }

        this.Id = id;
        this.MentionConfigurationId = mentionConfigurationId;
        this.LastScannedAt = lastScannedAt;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>FK to the mention configuration this watermark belongs to.</summary>
    public Guid MentionConfigurationId { get; init; }

    /// <summary>
    ///     Latest ADO PR last-update time observed; passed as <c>minLastUpdateDate</c>
    ///     on the next scan cycle.
    /// </summary>
    public DateTimeOffset LastScannedAt { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
