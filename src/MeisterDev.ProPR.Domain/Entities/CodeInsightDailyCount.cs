// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     One day's count of one measured dimension, at the finest scope a finding has. The five reporting
///     grains (job, pull request, file, repository, client) and the week and month buckets are all derived
///     from these rows by grouping, rather than each being materialised.
/// </summary>
/// <remarks>
///     <para>
///         Materialising every grain against every bucket size would mean roughly fifteen writes per finding
///         per dimension, and fifteen places a count could be wrong. One row per (scope, day, dimension) keeps
///         writes bounded and leaves exactly one place to get right; the scope parts are real columns, so
///         every grain is a <c>GROUP BY</c> over an index rather than a parsed key.
///     </para>
///     <para>
///         <see cref="BucketDate" /> is anchored to when the review <em>happened</em>, never to when a row was
///         written. A disposition observed weeks after the review still lands in the review's bucket,
///         otherwise a quality trend would move retroactively for reasons nobody could explain.
///     </para>
///     <para>
///         Every key part is non-null, empty string standing in for "not applicable". PostgreSQL treats NULLs
///         as distinct in a unique index, so a nullable key part would silently defeat the upsert and split one
///         counter into many: the same trap <c>ClientTokenUsageSample</c> documents.
///     </para>
/// </remarks>
public sealed class CodeInsightDailyCount
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning client. Always present: it is the tenancy boundary every query filters on.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Provider repository identifier.</summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>Provider pull-request identifier.</summary>
    public long PullRequestId { get; set; }

    /// <summary>
    ///     File the findings counted here are anchored to; the empty string for pull-request-level findings,
    ///     which is a real category rather than missing data.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>The review job the findings counted here came from.</summary>
    public Guid JobId { get; set; }

    /// <summary>
    ///     UTC date the counted findings were observed by the review that produced them. The time axis every
    ///     trend is built on.
    /// </summary>
    public DateOnly BucketDate { get; set; }

    /// <summary>Which quantity this row counts.</summary>
    public CodeInsightCountDimension Dimension { get; set; }

    /// <summary>
    ///     Which member of the dimension this row counts: a core type slug, a disposition name, or the empty
    ///     string when the dimension has no members (a plain finding total).
    /// </summary>
    public string DimensionKey { get; set; } = string.Empty;

    /// <summary>The count. Recomputed from the source rows on every touch rather than incremented.</summary>
    public int Count { get; set; }

    /// <summary>UTC timestamp when this row was last recomputed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
