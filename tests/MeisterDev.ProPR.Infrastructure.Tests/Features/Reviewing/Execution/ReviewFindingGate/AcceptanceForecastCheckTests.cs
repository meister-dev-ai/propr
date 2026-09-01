// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.ReviewFindingGate;

/// <summary>
///     Tests for <see cref="AcceptanceForecastCheck" />: the two deterministic dismiss classes (generated-file
///     targets and skipped-test critiques) produce a dismiss-forecast observation while leaving the decision
///     unchanged; every other finding passes through without an observation. The check must never alter a
///     disposition — it is observe-only by contract.
/// </summary>
public sealed class AcceptanceForecastCheckTests
{
    private const string FindingId = "finding-1";

    private static readonly AcceptanceForecastCheck Sut = new();

    private static CandidateReviewFinding Finding(string filePath, string message)
    {
        return new CandidateReviewFinding(
            FindingId,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", filePath),
            CommentSeverity.Warning,
            message,
            CandidateReviewFinding.PerFileCommentCategory,
            filePath,
            12);
    }

    private static FinalGateDecision Publish()
    {
        return new FinalGateDecision(
            FindingId,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.DefaultPublish],
            "default_publish_rules",
            [],
            null,
            null);
    }

    private static FinalGateDecision SummaryOnly()
    {
        return new FinalGateDecision(
            FindingId,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.WeakBroadFinding],
            "broad_finding_rules",
            [],
            null,
            "Broad concern noted.");
    }

    [Fact]
    public void Evaluate_GeneratedFileTarget_ForecastsDismissWithoutChangingTheDecision()
    {
        var decision = Publish();

        var outcome = Sut.Evaluate(Finding("go.work.sum", "This adds a second identical checksum entry."), decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Equal(AcceptanceForecastCheck.DismissForecastOutcome, outcome.Observation?.Outcome);
        Assert.Contains("generated", outcome.Observation?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_SkippedTestCritique_ForecastsDismissWithoutChangingTheDecision()
    {
        var decision = Publish();

        var outcome = Sut.Evaluate(
            Finding("pkg/tests/apis/playlist/playlist_test.go", "This new subtest is unconditionally skipped, so the path is untested."),
            decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Equal(AcceptanceForecastCheck.DismissForecastOutcome, outcome.Observation?.Outcome);
    }

    [Fact]
    public void Evaluate_OrdinarySourceFinding_PassesThroughWithoutObservation()
    {
        var decision = Publish();

        var outcome = Sut.Evaluate(Finding("src/service.go", "This lock was removed, so two builders can race."), decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Null(outcome.Observation);
    }

    [Fact]
    public void Evaluate_SkipWordOnSourceFile_PassesThroughWithoutObservation()
    {
        // The skipped-test class is confined to test files: skip vocabulary in production code (a skipped
        // element in a loop, say) is not a coverage critique.
        var outcome = Sut.Evaluate(Finding("src/worker.go", "Items with no shard are skipped here."), Publish());

        Assert.Null(outcome.Observation);
    }

    [Fact]
    public void Evaluate_NonPublishDisposition_PassesThroughUntouched()
    {
        var decision = SummaryOnly();

        var outcome = Sut.Evaluate(Finding("go.work.sum", "Duplicate checksum entry."), decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Null(outcome.Observation);
    }
}
