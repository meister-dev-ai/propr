// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Verification;

public sealed class EvidenceBundleTests
{
    [Fact]
    public void EvidenceBundle_WithCompleteCoverage_ReportsComplete()
    {
        var bundle = new EvidenceBundle(
            "claim-001",
            [new EvidenceItem("FileContentRange", "Fetched supporting file.")],
            EvidenceBundle.CompleteCoverage);

        Assert.True(bundle.HasCompleteCoverage);
    }

    [Fact]
    public void EvidenceBundle_PreservesAttemptsAndProCursorStatus()
    {
        var attempt = new EvidenceAttemptRecord(
            "claim-001:attempt:001",
            "claim-001",
            EvidenceAttemptRecord.ProCursorKnowledgeSource,
            1,
            EvidenceAttemptRecord.EmptyStatus,
            "Queried ProCursor knowledge.",
            EvidenceAttemptRecord.NoChangeCoverageImpact,
            failureReason: "No matches.");

        var bundle = new EvidenceBundle(
            "claim-001",
            [],
            EvidenceBundle.MissingCoverage,
            evidenceAttempts: [attempt],
            hasProCursorAttempt: true,
            proCursorResultStatus: EvidenceAttemptRecord.EmptyStatus);

        Assert.True(bundle.HasProCursorAttempt);
        Assert.Equal(EvidenceAttemptRecord.EmptyStatus, bundle.ProCursorResultStatus);
        Assert.Equal(attempt, Assert.Single(bundle.EvidenceAttempts));
    }

    [Fact]
    public void EvidenceItem_WithoutKind_Throws()
    {
        Assert.Throws<ArgumentException>(() => new EvidenceItem(string.Empty, "summary"));
    }
}
