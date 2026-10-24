// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Offline;

public sealed class JsonEvaluationArtifactWriterTests
{
    [Fact]
    public async Task WriteAsync_WritesVariantIdentityAndPromptExperimentEvidence()
    {
        var artifact = new EvaluationArtifact(
            new EvaluationRunMetadata(
                "run-variant",
                DateTimeOffset.Parse("2026-05-19T10:00:00Z"),
                DateTimeOffset.Parse("2026-05-19T10:01:00Z"),
                "completed",
                "resolved"),
            new EvaluationFixtureMetadata("fixture-001", "1.0", "synthetic"),
            new EvaluationConfigurationMetadata(
                "config-a",
                "gpt-5.4",
                "full",
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["baselineOnly"] = 0,
                    ["proRvOnly"] = 0,
                    ["both"] = 0,
                },
                "per-file-shorter-user",
                [PromptStageKeys.PerFileUser],
                true),
            new ReviewResult(
                "summary", [
                    new ReviewComment("src/Work.cs", 7, CommentSeverity.Error, "Unscoped delete.")
                    {
                        FindingClassification = new FindingTypeClassification(
                            ["logic-error"], [], CodeInsightFindingLevel.Member,
                            CodeInsightFindingQualifier.Incorrect, 0.93, 1, FindingTypeClassification.ReviewSource),
                        SuggestedFix = new ReviewSuggestedFix(7, 7, "Delete();", "Delete(tenantId);", "head", "src/Work.cs", true, 7),
                        RiskAssessment = new FindingRiskAssessment(
                            FindingImpact.Critical, FindingLikelihood.Likely, "Deletes tenant data.", "Every delete uses this query.", "review_model",
                            FindingRiskAssessment.CurrentRubricVersion),
                    }
                ]),
            [
                new StageEvidenceRecord(
                    "src/Foo.cs",
                    "src/Foo.cs",
                    "src/Foo.cs",
                    "Completed",
                    1,
                    0,
                    10,
                    5,
                    null,
                    "gpt-5.4",
                    AiConnectionModelCategory.Default,
                    [
                        new StageEvidenceEvent(
                            ProtocolEventKind.AiCall,
                            "ai_call_iter_1",
                            DateTimeOffset.Parse("2026-05-19T10:00:10Z"),
                            10,
                            5,
                            "user prompt",
                            "system prompt",
                            "response",
                            null),
                    ],
                    new PromptExperimentEvidence(
                        PromptStageKeys.PerFileUser,
                        "per-file-shorter-user",
                        PromptCompositionMode.Replace,
                        false,
                        "system prompt",
                        "user prompt")),
            ],
            new EvaluationTokenUsage(10, 5, [new EvaluationTokenUsageBreakdown("gpt-5.4", 10, 5)], [new EvaluationTokenUsageBreakdown("Default", 10, 5)]),
            []);

        var sut = new JsonEvaluationArtifactWriter();
        var outputPath = Path.Combine(Path.GetTempPath(), $"artifact-{Guid.NewGuid():N}.json");

        try
        {
            await sut.WriteAsync(artifact, outputPath, CancellationToken.None);

            await using var stream = File.OpenRead(outputPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);
            var root = document.RootElement;

            var risk = root.GetProperty("finalResult").GetProperty("comments")[0].GetProperty("riskAssessment");
            Assert.Equal("critical", risk.GetProperty("severity").GetString());
            Assert.Equal("critical", risk.GetProperty("impact").GetString());
            Assert.Equal("likely", risk.GetProperty("likelihood").GetString());
            Assert.Equal("Deletes tenant data.", risk.GetProperty("impactReason").GetString());
            var classification = root.GetProperty("finalResult").GetProperty("comments")[0].GetProperty("findingClassification");
            Assert.Equal("logic-error", classification.GetProperty("coreSlugs")[0].GetString());
            Assert.Equal(0.93, classification.GetProperty("confidence").GetDouble());
            Assert.Equal(FindingTypeClassification.ReviewSource, classification.GetProperty("source").GetString());
            var fix = root.GetProperty("finalResult").GetProperty("comments")[0].GetProperty("suggestedFix");
            Assert.Equal("Delete(tenantId);", fix.GetProperty("replacementText").GetString());
            Assert.Equal("head", fix.GetProperty("sourceHeadSha").GetString());
            Assert.Equal(7, fix.GetProperty("sourceAnchorLineNumber").GetInt32());

            Assert.Equal("per-file-shorter-user", root.GetProperty("configuration").GetProperty("variantName").GetString());
            Assert.True(root.GetProperty("configuration").GetProperty("usedPromptExperiment").GetBoolean());
            Assert.Equal(
                PromptStageKeys.PerFileUser,
                root.GetProperty("configuration").GetProperty("targetedStageKeys")[0].GetString());

            var evidence = root.GetProperty("stages")[0].GetProperty("promptExperimentEvidence");
            Assert.Equal(PromptStageKeys.PerFileUser, evidence.GetProperty("stageKey").GetString());
            Assert.Equal("per-file-shorter-user", evidence.GetProperty("variantName").GetString());
            Assert.Equal("replace", evidence.GetProperty("compositionMode").GetString());
            Assert.False(evidence.GetProperty("usedDefaultConstruction").GetBoolean());
            Assert.Equal("system prompt", evidence.GetProperty("systemPromptText").GetString());
            Assert.Equal("user prompt", evidence.GetProperty("userPromptText").GetString());
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_WritesPerStagePromptEvidenceForMultipleExecutedStages()
    {
        var artifact = new EvaluationArtifact(
            new EvaluationRunMetadata(
                "run-variant",
                DateTimeOffset.Parse("2026-05-19T10:00:00Z"),
                DateTimeOffset.Parse("2026-05-19T10:01:00Z"),
                "completed",
                "resolved"),
            new EvaluationFixtureMetadata("fixture-001", "1.0", "synthetic"),
            new EvaluationConfigurationMetadata(
                "config-a",
                "gpt-5.4",
                "full",
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["baselineOnly"] = 0,
                    ["proRvOnly"] = 0,
                    ["both"] = 0,
                },
                "variant-a",
                [PromptStageKeys.PerFileUser, PromptStageKeys.SynthesisSystem],
                true),
            new ReviewResult("summary", []),
            [
                new StageEvidenceRecord(
                    "src/Foo.cs",
                    "src/Foo.cs",
                    "src/Foo.cs",
                    "Completed",
                    1,
                    0,
                    10,
                    5,
                    null,
                    "gpt-5.4",
                    AiConnectionModelCategory.Default,
                    [
                        new StageEvidenceEvent(
                            ProtocolEventKind.Operational,
                            ReviewProtocolEventNames.PromptStageEvidenceRecorded,
                            DateTimeOffset.Parse("2026-05-19T10:00:10Z"),
                            null,
                            null,
                            "details",
                            null,
                            "output",
                            null),
                    ],
                    new PromptExperimentEvidence(
                        PromptStageKeys.PerFileUser,
                        "variant-a",
                        PromptCompositionMode.Replace,
                        false,
                        "system prompt",
                        "user prompt")),
                new StageEvidenceRecord(
                    "synthesis",
                    "synthesis",
                    null,
                    "Completed",
                    1,
                    0,
                    10,
                    5,
                    null,
                    "gpt-5.4",
                    AiConnectionModelCategory.Default,
                    [
                        new StageEvidenceEvent(
                            ProtocolEventKind.Operational,
                            ReviewProtocolEventNames.PromptStageEvidenceRecorded,
                            DateTimeOffset.Parse("2026-05-19T10:00:20Z"),
                            null,
                            null,
                            "details-2",
                            null,
                            "output-2",
                            null),
                    ],
                    new PromptExperimentEvidence(
                        PromptStageKeys.SynthesisSystem,
                        "variant-a",
                        PromptCompositionMode.Append,
                        false,
                        "synthesis system",
                        "synthesis user")),
            ],
            new EvaluationTokenUsage(10, 5, [new EvaluationTokenUsageBreakdown("gpt-5.4", 10, 5)], [new EvaluationTokenUsageBreakdown("Default", 10, 5)]),
            []);

        var sut = new JsonEvaluationArtifactWriter();
        var outputPath = Path.Combine(Path.GetTempPath(), $"artifact-{Guid.NewGuid():N}.json");

        try
        {
            await sut.WriteAsync(artifact, outputPath, CancellationToken.None);

            await using var stream = File.OpenRead(outputPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: CancellationToken.None);
            var stages = document.RootElement.GetProperty("stages");

            Assert.Equal(2, stages.GetArrayLength());
            Assert.Equal(PromptStageKeys.PerFileUser, stages[0].GetProperty("promptExperimentEvidence").GetProperty("stageKey").GetString());
            Assert.Equal(PromptStageKeys.SynthesisSystem, stages[1].GetProperty("promptExperimentEvidence").GetProperty("stageKey").GetString());
            Assert.Equal("append", stages[1].GetProperty("promptExperimentEvidence").GetProperty("compositionMode").GetString());
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
