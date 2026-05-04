// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Structured evidence metadata attached to a candidate finding.
/// </summary>
public sealed record EvidenceReference
{
    public const string ResolvedState = "resolved";
    public const string MissingState = "missing";
    public const string PartialState = "partial";

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

    public IReadOnlyList<string> SupportingFindingIds { get; }

    public IReadOnlyList<string> SupportingFiles { get; }

    public string EvidenceResolutionState { get; }

    public string EvidenceSource { get; }

    public bool HasResolvedMultiFileEvidence =>
        string.Equals(this.EvidenceResolutionState, ResolvedState, StringComparison.Ordinal) &&
        this.SupportingFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
}
