// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Records that a thread has been acted on at one observed non-reviewer comment count, at one revision.
/// </summary>
/// <remarks>
///     <para>
///         Written after the reply or the status change was published, because progress may only advance for
///         work that finished: a claim written first turns a provider that returned 429 into a thread that is
///         never answered and never retried.
///     </para>
///     <para>
///         The revision is part of the identity. A finding nobody has replied to carries an observed count of
///         zero for the life of the pull request, so without the revision the first pass to judge it would
///         record the pair and every later push would skip it, which is the opposite of what pushing a fix is
///         supposed to achieve. The record outlives the pass that wrote it, so a later pass over the same pull
///         request at the same revision finds it.
///     </para>
/// </remarks>
public sealed class ThreadPassHandledThread
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>FK to the pass that recorded this.</summary>
    public Guid ThreadPassJobId { get; set; }

    /// <summary>The client that owns the pull request.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Provider-native repository identifier.</summary>
    /// <summary>The host that issued <see cref="RepositoryId" />, and so the scope it is unique within.</summary>
    public string OrganizationUrl { get; set; } = string.Empty;

    /// <summary>The project within the host, empty where the host addresses repositories without one.</summary>
    public string ProjectId { get; set; } = string.Empty;

    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>Provider pull request number.</summary>
    public int PullRequestId { get; set; }

    /// <summary>Provider-native thread identifier.</summary>
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>The non-reviewer comment count observed on the thread when the pass acted on it.</summary>
    public int ObservedReplyCount { get; set; }

    /// <summary>The stored revision key the pass was running at when it acted on the thread.</summary>
    public string RevisionKey { get; set; } = string.Empty;

    /// <summary>When the record was written.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>Navigation property back to the owning pass.</summary>
    public ThreadPassJob ThreadPassJob { get; set; } = null!;
}
