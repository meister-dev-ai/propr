// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Something a human reviewer raised that ProPR did not: a false negative. Without these, precision is
///     the only thing measurable, and precision alone flatters any reviewer that simply says less.
/// </summary>
/// <remarks>
///     Lives under the same pull-request aggregate as findings, so retention and the client cascade are
///     shared rather than reimplemented. The three judgements are stored separately from the verdict on
///     purpose: a threshold change can then be re-applied to what was already harvested instead of
///     re-judging every thread through the model again.
/// </remarks>
public sealed class CodeInsightMiss
{
    /// <summary>Unique identifier for this record.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning aggregate: the pull request the human comment was made on.</summary>
    public Guid CodeInsightPullRequestId { get; init; }

    /// <summary>Navigation to the owning aggregate.</summary>
    public CodeInsightPullRequest? CodeInsightPullRequest { get; init; }

    /// <summary>Provider thread the human comment belongs to. The identity used to avoid harvesting it twice.</summary>
    public string ProviderThreadId { get; init; } = string.Empty;

    /// <summary>File the comment was anchored to, or <see langword="null" /> for a pull-request-level thread.</summary>
    public string? FilePath { get; init; }

    /// <summary>Line the comment was anchored to, or <see langword="null" /> when unknown.</summary>
    public int? LineNumber { get; init; }

    /// <summary>The discussion the judgement was made from, encrypted at rest.</summary>
    public string EncryptedDiscussion { get; init; } = string.Empty;

    /// <summary>Whether the thread was judged to describe a substantive code issue rather than a question or nit.</summary>
    public bool IsSubstantive { get; init; }

    /// <summary>Whether the thread was judged to have been accepted or to have led to a code change.</summary>
    public bool WasActedOn { get; init; }

    /// <summary>
    ///     Whether the issue was judged to be within the class ProPR should reasonably catch. The cut-off this
    ///     encodes is a calibration decision, which is why the judgement is stored rather than only its effect.
    /// </summary>
    public bool IsInScope { get; init; }

    /// <summary>
    ///     Whether all three judgements held and the thread did not restate a finding ProPR raised: that is,
    ///     whether this counts toward recall. Stored so a threshold change can be re-applied without
    ///     re-judging.
    /// </summary>
    public bool CountsAsMiss { get; init; }

    /// <summary>The classifier's confidence in its judgements, 0–1.</summary>
    public double? ClassifierConfidence { get; init; }

    /// <summary>Identifier of the classifier that judged this thread.</summary>
    public string ClassifierVersion { get; init; } = string.Empty;

    /// <summary>UTC timestamp when the thread was harvested.</summary>
    public DateTimeOffset HarvestedAt { get; init; }
}
