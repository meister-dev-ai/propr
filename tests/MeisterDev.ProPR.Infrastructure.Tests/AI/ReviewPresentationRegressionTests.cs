// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class ReviewPresentationRegressionTests
{
    [Fact]
    public void FileConfidence_DoesNotReclassifyIndividualDefectsAsImprovements()
    {
        var comments = new[]
        {
            new ReviewComment("a.cs", 1, CommentSeverity.Error, "Confirmed rare defect."),
            new ReviewComment("b.cs", 2, CommentSeverity.Warning, "Unverified caller."),
            new ReviewComment("c.cs", 3, CommentSeverity.Suggestion, "Extract the repeated validation."),
        };
        var result = FileByFileReviewOrchestrator.ApplyConfidenceFloor(new ReviewResult("Review.", comments), 0, new AiReviewOptions());
        Assert.Equal(comments.Select(comment => comment.Severity), result.Comments.Select(comment => comment.Severity));
        Assert.Equal(comments.Select(comment => comment.Message), result.Comments.Select(comment => comment.Message));
    }

    [Fact]
    public void GitLab_PreservesCodeBytesInFinding()
    {
        const string code = "if (count < 2 && value == \"raw\") return new List<T>();";
        var body = GitLabFindingCommentRenderer.Format(
            new ReviewComment("a.cs", 1, CommentSeverity.Error, "Replace the condition.\n```csharp\n" + code + "\n```"));
        Assert.Contains(code, body);
        Assert.DoesNotContain("&lt;", body);
        Assert.DoesNotContain("&quot;", body);
    }

    [Fact]
    public void Summary_OrdersAssessedRiskWithoutChangingStoredFindingOrder()
    {
        var low = Finding("low.cs", FindingImpact.Low, FindingLikelihood.Unlikely);
        var high = Finding("high.cs", FindingImpact.Critical, FindingLikelihood.Likely);
        var comments = new[] { low, high };
        var body = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", comments));
        Assert.Contains("high.cs", body);
        Assert.Contains("low.cs", body);
        Assert.True(body.IndexOf("high.cs", StringComparison.Ordinal) < body.IndexOf("low.cs", StringComparison.Ordinal));
        Assert.Contains("Risk: Critical", body);
        Assert.Same(low, comments[0]);
    }

    private static ReviewComment Finding(string path, FindingImpact impact, FindingLikelihood likelihood) =>
        new(path, 1, CommentSeverity.Error, "The input loses data.")
        {
            RiskAssessment = new FindingRiskAssessment(
                impact, likelihood, "Data loss.", "On this input.", "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };
}
