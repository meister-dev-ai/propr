// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Tracks the project-level watermarks for mention scanning: when the configuration was last scanned, and
///     how far back a scan is still sure of. One row per mention configuration.
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
    ///     When this configuration was last scanned, whatever came of it. What the configuration's own scan
    ///     interval is measured from.
    /// </summary>
    public DateTimeOffset LastScannedAt { get; set; }

    /// <summary>
    ///     When a scan last covered every repository this configuration claims, or <see langword="null" />
    ///     when none has yet. Where the next scan's discovery window starts.
    /// </summary>
    /// <remarks>
    ///     Apart from <see cref="LastScannedAt" /> because the two answer different questions and a tick can
    ///     move one without the other. A throttled provider, a repository that has gone, or a connection that
    ///     could not be opened leaves part of the window unread; the configuration has still been scanned, so
    ///     its interval must advance or every tick would scan it again, but the window it failed to read must
    ///     stay open or a question asked in it is never seen. Null on a row written before this was recorded,
    ///     which falls back to <see cref="LastScannedAt" /> and so carries an installation across unchanged.
    /// </remarks>
    public DateTimeOffset? LastCompleteScanAt { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
