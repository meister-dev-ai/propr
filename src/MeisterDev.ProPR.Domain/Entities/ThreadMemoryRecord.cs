// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Represents one stored embedding for a resolved PR review thread within a client's memory store.
///     The <see cref="EmbeddingVector" /> is stored as <c>float[]</c> in the domain and application layer;
///     the Infrastructure repository implementation converts to the provider-specific type (e.g. pgvector
///     <c>Vector</c>) at the persistence boundary.
/// </summary>
public sealed class ThreadMemoryRecord
{
    /// <summary>Unique identifier for this record.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning client — scopes the record. Must not be <see cref="Guid.Empty" />.</summary>
    public Guid ClientId { get; init; }

    /// <summary>ADO thread identifier. Must be &gt; 0. Unique per (ClientId, RepositoryId, ThreadId).</summary>
    public long ThreadId { get; init; }

    /// <summary>ADO repository identifier. Required, ≤ 256 characters.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>ADO pull request number. Must be &gt; 0.</summary>
    public int PullRequestId { get; init; }

    /// <summary>File path the thread was anchored to. Null for PR-level threads. ≤ 512 characters.</summary>
    public string? FilePath { get; init; }

    /// <summary>Truncated diff excerpt relevant to the thread (≤ 2,000 chars). Null if not available.</summary>
    public string? ChangeExcerpt { get; init; }

    /// <summary>Serialised comment history (author + content pairs, condensed).</summary>
    public string CommentHistoryDigest { get; init; } = string.Empty;

    /// <summary>AI-generated 2–4 sentence summary of how the thread was resolved.</summary>
    public string ResolutionSummary { get; init; } = string.Empty;

    /// <summary>
    ///     Cosine-similarity search vector over the composite content.
    ///     Stored as <c>float[]</c> in Domain/Application; the Infrastructure implementation converts to
    ///     the provider-specific type (e.g. pgvector <c>Vector</c>) at the persistence boundary.
    /// </summary>
    public float[] EmbeddingVector { get; init; } = [];

    /// <summary>UTC timestamp when the record was first created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC timestamp when the record was last upserted.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    ///     Indicates how this memory record was created.
    ///     <see cref="Enums.MemorySource.ThreadResolved" /> for records created by the crawl state machine;
    ///     <see cref="Enums.MemorySource.AdminDismissed" /> for records created by an admin explicitly dismissing a finding.
    ///     Defaults to <see cref="Enums.MemorySource.ThreadResolved" /> for backward compatibility with existing records.
    /// </summary>
    public MemorySource MemorySource { get; init; } = MemorySource.ThreadResolved;

    /// <summary>
    ///     The durable code-insight finding this memory came from, when one is known; <see langword="null" />
    ///     otherwise, for an admin-dismissed record, a thread raised before insight collection was enabled,
    ///     or a client that does not collect insights at all.
    ///     Held <em>by value</em>, with no foreign key: the two stores keep independent lifecycles, so a purge
    ///     on either side must not cascade into the other.
    /// </summary>
    public Guid? CodeInsightFindingId { get; set; }

    /// <summary>
    ///     Human-searchable keywords for this memory. Display and search metadata only: the similarity
    ///     matching path uses <see cref="EmbeddingVector" /> and is unaffected by these.
    ///     Empty when none have been extracted; existing records without keywords stay valid.
    /// </summary>
    public List<string> Keywords { get; set; } = [];

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

        if (this.ThreadId <= 0)
        {
            throw new ArgumentException("ThreadId must be > 0.");
        }

        if (string.IsNullOrWhiteSpace(this.RepositoryId))
        {
            throw new ArgumentException("RepositoryId must not be null or whitespace.");
        }

        if (this.PullRequestId <= 0)
        {
            throw new ArgumentException("PullRequestId must be > 0.");
        }

        if (string.IsNullOrEmpty(this.ResolutionSummary))
        {
            throw new ArgumentException("ResolutionSummary must not be null or empty.");
        }

        if (this.EmbeddingVector is null || this.EmbeddingVector.Length == 0)
        {
            throw new ArgumentException("EmbeddingVector must not be null or empty.");
        }
    }
}
