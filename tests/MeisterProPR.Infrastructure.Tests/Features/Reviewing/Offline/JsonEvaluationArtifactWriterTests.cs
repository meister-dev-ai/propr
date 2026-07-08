// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Offline;

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
                ReviewStrategy.FileByFile,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["baselineOnly"] = 0,
                    ["proRvOnly"] = 0,
                    ["both"] = 0,
                },
                "per-file-shorter-user",
                [PromptStageKeys.PerFileUser],
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
                ReviewStrategy.FileByFile,
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
