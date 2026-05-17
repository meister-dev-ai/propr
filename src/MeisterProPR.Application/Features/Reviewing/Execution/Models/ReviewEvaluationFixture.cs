// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Portable description of one review input that can be replayed without live SCM access.
/// </summary>
public sealed record ReviewEvaluationFixture(
    string FixtureId,
    string FixtureVersion,
    FixtureProvenance Provenance,
    RepositorySnapshot RepositorySnapshot,
    PullRequestSnapshot PullRequestSnapshot,
    IReadOnlyList<FixtureThread>? Threads = null,
    FixtureExpectations? Expectations = null,
    FixtureProRVPrefilterExpectations? ProRVPrefilterExpectations = null)
{
    /// <summary>Optional prior discussion threads supplied with the fixture.</summary>
    public IReadOnlyList<FixtureThread> ThreadsOrEmpty => this.Threads ?? [];

    /// <summary>Optional expected review outcomes used by evaluation verification.</summary>
    public FixtureExpectations? ExpectationsOrNull => this.Expectations;

    /// <summary>Optional expected ProRV prefilter outcomes used by deterministic ranked-item verification.</summary>
    public FixtureProRVPrefilterExpectations? ProRVPrefilterExpectationsOrNull => this.ProRVPrefilterExpectations;
}

/// <summary>
///     Optional expected outcomes used to evaluate whether a generated review hit the intended positives,
///     avoided the intended negatives, and covered important informational points.
/// </summary>
public sealed record FixtureExpectations(
    IReadOnlyList<FixtureExpectation>? PositiveExamples = null,
    IReadOnlyList<FixtureExpectation>? NegativeExamples = null,
    IReadOnlyList<FixtureExpectation>? InfoExamples = null)
{
    /// <summary>Positive examples the generated review should identify or reflect.</summary>
    public IReadOnlyList<FixtureExpectation> PositiveExamplesOrEmpty => this.PositiveExamples ?? [];

    /// <summary>Negative examples the generated review should avoid.</summary>
    public IReadOnlyList<FixtureExpectation> NegativeExamplesOrEmpty => this.NegativeExamples ?? [];

    /// <summary>Important informational points the generated review should capture.</summary>
    public IReadOnlyList<FixtureExpectation> InfoExamplesOrEmpty => this.InfoExamples ?? [];

    /// <summary>Returns true when at least one expectation exists.</summary>
    public bool HasAny => this.PositiveExamplesOrEmpty.Count > 0
                          || this.NegativeExamplesOrEmpty.Count > 0
                          || this.InfoExamplesOrEmpty.Count > 0;
}

/// <summary>
///     One fixture expectation entry with a stable identifier and natural-language description.
/// </summary>
public sealed record FixtureExpectation(string Key, string Description);

/// <summary>
///     Optional expected ProRV prefilter outcomes for one fixture.
/// </summary>
public sealed record FixtureProRVPrefilterExpectations(IReadOnlyList<FixtureProRVPrefilterPositiveExample>? PositiveExamples = null)
{
    /// <summary>Positive examples the ProRV prefilter should hit for the corresponding changed file.</summary>
    public IReadOnlyList<FixtureProRVPrefilterPositiveExample> PositiveExamplesOrEmpty => this.PositiveExamples ?? [];

    /// <summary>Returns true when at least one ProRV prefilter expectation exists.</summary>
    public bool HasAny => this.PositiveExamplesOrEmpty.Count > 0;
}

/// <summary>
///     One deterministic ProRV prefilter expectation for a specific changed file.
/// </summary>
public sealed record FixtureProRVPrefilterPositiveExample(
    string Key,
    string FilePath,
    IReadOnlyList<string>? ExpectedItemIds,
    string Description)
{
    /// <summary>
    ///     Acceptable ProRV item identifiers for this expected issue. The expectation is fulfilled when at least one
    ///     of these identifiers is present in the ranked output for the file. An empty list means the correct behavior
    ///     is to return no ProRV items for the file.
    /// </summary>
    public IReadOnlyList<string> ExpectedItemIdsOrEmpty => this.ExpectedItemIds ?? [];
}

/// <summary>
///     Describes where the fixture content originated.
/// </summary>
public sealed record FixtureProvenance(string SourceKind, string? SourceReference = null);

/// <summary>
///     Repository snapshot used by offline review-context tools.
/// </summary>
public sealed record RepositorySnapshot(
    string SourceBranch,
    string TargetBranch,
    IReadOnlyList<RepositoryFileEntry> Files,
    string? RepositoryName = null);

/// <summary>
///     One file in the offline repository snapshot.
/// </summary>
public sealed record RepositoryFileEntry(
    string Path,
    string Content,
    bool IsBinary = false,
    string? OriginalPath = null);

/// <summary>
///     Provider-neutral pull-request state mapped into the existing review workflow.
/// </summary>
public sealed record PullRequestSnapshot(
    CodeReviewRef CodeReview,
    ReviewRevision Revision,
    string Title,
    string? Description,
    string SourceBranch,
    string TargetBranch,
    IReadOnlyList<FixtureChangedFile> ChangedFiles,
    IReadOnlyList<ChangedFileSummary>? AllChangedFileSummaries = null,
    Guid? AuthorizedIdentityId = null);

/// <summary>
///     One changed file supplied by the offline fixture.
/// </summary>
public sealed record FixtureChangedFile(
    string Path,
    ChangeType ChangeType,
    string UnifiedDiff,
    string FullContent,
    bool IsBinary = false,
    string? OriginalPath = null);

/// <summary>
///     Prior discussion thread supplied with the offline fixture.
/// </summary>
public sealed record FixtureThread(
    long ThreadId,
    string? FilePath,
    int? LineNumber,
    string? Status,
    IReadOnlyList<FixtureThreadComment> Comments);

/// <summary>
///     One discussion comment inside a fixture thread.
/// </summary>
public sealed record FixtureThreadComment(
    string AuthorName,
    string Content,
    Guid? AuthorId = null,
    long CommentId = 0,
    DateTimeOffset? PublishedAt = null);
