// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Structured evidence metadata attached to a candidate finding.
/// </summary>
public sealed record EvidenceReference
{
    /// <summary>
    ///     Resolution state indicating evidence was fully resolved.
    /// </summary>
    public const string ResolvedState = "resolved";

    /// <summary>
    ///     Resolution state indicating evidence is missing.
    /// </summary>
    public const string MissingState = "missing";

    /// <summary>
    ///     Resolution state indicating evidence is only partially resolved.
    /// </summary>
    public const string PartialState = "partial";

    /// <summary>
    ///     Initializes structured evidence metadata for a candidate finding.
    /// </summary>
    /// <param name="supportingFindingIds">Identifiers of findings that contribute evidence.</param>
    /// <param name="supportingFiles">Repository-relative files that contribute evidence.</param>
    /// <param name="evidenceResolutionState">Resolution state for the evidence.</param>
    /// <param name="evidenceSource">Source family that produced the evidence.</param>
    public EvidenceReference(
        IReadOnlyList<string>? supportingFindingIds,
        IReadOnlyList<string>? supportingFiles,
        string evidenceResolutionState,
        string evidenceSource)
    {
        if (string.IsNullOrWhiteSpace(evidenceResolutionState))
        {
            throw new ArgumentException("Evidence resolution state is required.", nameof(evidenceResolutionState));
        }

        if (string.IsNullOrWhiteSpace(evidenceSource))
        {
            throw new ArgumentException("Evidence source is required.", nameof(evidenceSource));
        }

        this.SupportingFindingIds = supportingFindingIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        this.SupportingFiles = supportingFiles?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? [];
        this.EvidenceResolutionState = evidenceResolutionState;
        this.EvidenceSource = evidenceSource;
    }

    /// <summary>
    ///     Gets the identifiers of supporting findings.
    /// </summary>
    public IReadOnlyList<string> SupportingFindingIds { get; }

    /// <summary>
    ///     Gets the repository-relative supporting files.
    /// </summary>
    public IReadOnlyList<string> SupportingFiles { get; }

    /// <summary>
    ///     Gets the resolution state for the evidence.
    /// </summary>
    public string EvidenceResolutionState { get; }

    /// <summary>
    ///     Gets the source family that produced the evidence.
    /// </summary>
    public string EvidenceSource { get; }

    /// <summary>
    ///     Gets a value indicating whether resolved evidence spans multiple files.
    /// </summary>
    public bool HasResolvedMultiFileEvidence =>
        string.Equals(this.EvidenceResolutionState, ResolvedState, StringComparison.Ordinal) &&
        this.SupportingFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
}
