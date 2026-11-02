// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Prepared fragments whose escaping and provider policy are resolved before template composition.</summary>
internal static class ReviewPresentationModels
{
    internal sealed record Finding(string Header, string Message, string? Assessment);

    internal sealed record AssessmentRow(string Dimension, string Level, string Reason);

    internal sealed record Assessment(IReadOnlyList<AssessmentRow> Rows, bool Collapsed = false);

    internal sealed record SummaryDetails(string? Location, string Body);

    internal sealed record Summary(
        IReadOnlyList<string> Entries,
        string? Note,
        int FindingCount,
        string RiskDistribution,
        string ConfidenceDistribution)
    {
        public bool HasFindings => this.Entries.Count > 0;

        public string LineBreak => Environment.NewLine;
    }
}
