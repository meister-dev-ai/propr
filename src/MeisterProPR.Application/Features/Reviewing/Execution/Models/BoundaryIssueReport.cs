// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Captures one Reviewing boundary or ownership issue discovered during the restructure.</summary>
public sealed record BoundaryIssueReport
{
    /// <summary>Initializes one boundary issue report.</summary>
    public BoundaryIssueReport(
        string issueId,
        string affectedComponent,
        string issueType,
        string impactSummary,
        string resolutionState,
        string recommendedOwner)
    {
        if (string.IsNullOrWhiteSpace(issueId))
        {
            throw new ArgumentException("Issue id is required.", nameof(issueId));
        }

        if (string.IsNullOrWhiteSpace(affectedComponent))
        {
            throw new ArgumentException("Affected component is required.", nameof(affectedComponent));
        }

        if (string.IsNullOrWhiteSpace(issueType))
        {
            throw new ArgumentException("Issue type is required.", nameof(issueType));
        }

        if (string.IsNullOrWhiteSpace(impactSummary))
        {
            throw new ArgumentException("Impact summary is required.", nameof(impactSummary));
        }

        if (string.IsNullOrWhiteSpace(resolutionState))
        {
            throw new ArgumentException("Resolution state is required.", nameof(resolutionState));
        }

        if (string.IsNullOrWhiteSpace(recommendedOwner))
        {
            throw new ArgumentException("Recommended owner is required.", nameof(recommendedOwner));
        }

        this.IssueId = issueId;
        this.AffectedComponent = affectedComponent;
        this.IssueType = issueType;
        this.ImpactSummary = impactSummary;
        this.ResolutionState = resolutionState;
        this.RecommendedOwner = recommendedOwner;
    }

    /// <summary>Stable issue identifier.</summary>
    public string IssueId { get; }

    /// <summary>Component or seam impacted by the issue.</summary>
    public string AffectedComponent { get; }

    /// <summary>Boundary issue category.</summary>
    public string IssueType { get; }

    /// <summary>Human-readable impact description.</summary>
    public string ImpactSummary { get; }

    /// <summary>Resolution state for the issue.</summary>
    public string ResolutionState { get; }

    /// <summary>Owning follow-up team or module.</summary>
    public string RecommendedOwner { get; }

    /// <summary>Creates a deferred boundary issue for a known unresolved seam.</summary>
    public static BoundaryIssueReport CreateDeferred(
        string issueId,
        string affectedComponent,
        string issueType,
        string impactSummary,
        string recommendedOwner)
    {
        return new BoundaryIssueReport(
            issueId,
            affectedComponent,
            issueType,
            impactSummary,
            ResolutionStates.Deferred,
            recommendedOwner);
    }

    /// <summary>Known stable issue identifiers for this feature slice.</summary>
    public static class KnownIssueIds
    {
        public const string InlineDispatcherSelectionBypass = "reviewing.inline-dispatcher-selection-bypass";
    }

    /// <summary>Known Reviewing boundary issue types.</summary>
    public static class IssueTypes
    {
        public const string DependencyDirection = "DependencyDirection";
        public const string OwnershipDrift = "OwnershipDrift";
        public const string PlacementMismatch = "PlacementMismatch";
        public const string SelectionBypass = "SelectionBypass";
    }

    /// <summary>Known resolution states for a boundary issue.</summary>
    public static class ResolutionStates
    {
        public const string Resolved = "Resolved";
        public const string Deferred = "Deferred";
        public const string AcceptedForFollowUp = "AcceptedForFollowUp";
    }
}
