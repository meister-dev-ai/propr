// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     One finding ProPR has already posted on a pull request, held so a later review increment can recognise
///     the same concern coming back and keep it off the pull request a second time.
/// </summary>
/// <remarks>
///     A corpus of its own, deliberately not thread memory. Thread memory is written only when a human resolves
///     a thread and is gated on the resolution being corroborated, which is correct for teaching a review what
///     was decided and useless for recognising a duplicate: a finding posted in one increment and still open in
///     the next has no memory record at all, and that is exactly the window in which it gets re-posted. This
///     corpus is written at posting time for every posted finding, open or not, and those gates are left alone.
/// </remarks>
public sealed class PostedFindingRecord
{
    /// <summary>Unique identifier for this record.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client, which scopes the record. Must not be <see cref="Guid.Empty" />.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Provider repository identifier. Required, at most 256 characters.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>Pull request number the finding was posted on. Must be greater than zero.</summary>
    public int PullRequestId { get; init; }

    /// <summary>
    ///     Provider thread the finding was posted as, as the provider itself writes it. The handle a later
    ///     increment reports as the duplicated thread.
    /// </summary>
    public string ProviderThreadId { get; init; } = string.Empty;

    /// <summary>Mirrors the stored column's bound, so an over-long value is refused before the insert.</summary>
    private const int MaxProviderThreadIdLength = 256;

    /// <summary>
    ///     The review job that posted the finding. Rows are written once, after that job has finished
    ///     publishing, so a lookup running inside a job can only ever see earlier jobs. That ordering is what
    ///     keeps this index strictly cross-increment and stops it competing with the per-job deduplication
    ///     that already governs a single review's own output.
    /// </summary>
    public Guid ReviewJobId { get; init; }

    /// <summary>Provider iteration the finding was posted against, carried so an operator can see which increment first raised it.</summary>
    public int IterationId { get; init; }

    /// <summary>
    ///     Whether ProPR closed this thread itself, through auto-resolve-by-severity, rather than a reviewer
    ///     closing it.
    /// </summary>
    /// <remarks>
    ///     Without this the two are indistinguishable at the provider: both leave the thread resolved as fixed.
    ///     A reviewer's fix means the code moved and the concern may genuinely recur, so it must not suppress.
    ///     ProPR closing its own thread means nobody adjudicated anything, so re-posting the same concern is
    ///     pure repetition and must be suppressed. Reading the provider status alone would silently disable
    ///     cross-increment protection for every client that uses auto-resolve.
    /// </remarks>
    public bool AutoResolvedByProPr { get; init; }

    /// <summary>
    ///     File the finding was anchored to, or <see langword="null" /> for a pull-request-level finding.
    ///     Context for a human reading the record. Deliberately not part of the match: the same concern has been
    ///     observed surfacing in a different file across increments.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    ///     Severity the finding carried when it was posted. Context only, and deliberately not part of the match:
    ///     the same defect has been observed posted as an error in one increment and a suggestion in another.
    /// </summary>
    public CommentSeverity Severity { get; init; }

    /// <summary>The finding text as the model wrote it, without the severity prefix the provider comment carries.</summary>
    public string FindingMessage { get; init; } = string.Empty;

    /// <summary>
    ///     Embedding of the finding text alone. Anchor-free, severity-free and file-free, because those are the
    ///     three things observed drifting between increments while the concern stayed the same.
    /// </summary>
    public float[] EmbeddingVector { get; init; } = [];

    /// <summary>UTC timestamp when the finding was posted and indexed.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    ///     Validates the record and throws <see cref="ArgumentException" /> for any violated rule.
    /// </summary>
    /// <exception cref="ArgumentException">When any validation rule is violated.</exception>
    public void Validate()
    {
        if (this.ClientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be Guid.Empty.");
        }

        if (string.IsNullOrWhiteSpace(this.RepositoryId))
        {
            throw new ArgumentException("RepositoryId must not be null or whitespace.");
        }

        if (this.PullRequestId <= 0)
        {
            throw new ArgumentException("PullRequestId must be > 0.");
        }

        if (string.IsNullOrWhiteSpace(this.FindingMessage))
        {
            throw new ArgumentException("FindingMessage must not be null or whitespace.");
        }

        // The thread identity carries the deduplication key. An absent one is not merely an incomplete record:
        // every finding missing it collides on the same key within a pull request, so all but the first are
        // silently discarded as duplicates of each other. The stored column is bounded, and a value the
        // database would reject has to be refused here rather than at the insert.
        if (string.IsNullOrWhiteSpace(this.ProviderThreadId))
        {
            throw new ArgumentException("ProviderThreadId must not be null or whitespace.");
        }

        if (this.ProviderThreadId.Length > MaxProviderThreadIdLength)
        {
            throw new ArgumentException($"ProviderThreadId must not exceed {MaxProviderThreadIdLength} characters.");
        }

        if (this.EmbeddingVector is null || this.EmbeddingVector.Length == 0)
        {
            throw new ArgumentException("EmbeddingVector must not be null or empty.");
        }
    }
}
