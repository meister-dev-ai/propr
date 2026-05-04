// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Writes evaluation artifacts as portable JSON documents.
/// </summary>
public sealed class JsonEvaluationArtifactWriter : IEvaluationArtifactWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    static JsonEvaluationArtifactWriter()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public async Task<string> WriteAsync(
        EvaluationArtifact artifact,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new
        {
            run = new
            {
                runId = artifact.Run.RunId,
                startedAt = artifact.Run.StartedAt,
                completedAt = artifact.Run.CompletedAt,
                outcome = artifact.Run.Outcome,
                protectedResolutionStatus = artifact.Run.ProtectedResolutionStatus,
            },
            fixture = new
            {
                fixtureId = artifact.Fixture.FixtureId,
                fixtureVersion = artifact.Fixture.FixtureVersion,
                provenance = artifact.Fixture.Provenance,
            },
            configuration = new
            {
                configurationId = artifact.Configuration.ConfigurationId,
                modelId = artifact.Configuration.ModelId,
                detailMode = artifact.Configuration.DetailMode,
            },
            finalResult = new
            {
                summary = artifact.FinalResult.Summary,
                comments = artifact.FinalResult.Comments.Select(comment => new
                {
                    filePath = comment.FilePath,
                    lineNumber = comment.LineNumber,
                    severity = comment.Severity.ToString().ToLowerInvariant(),
                    message = comment.Message,
                }),
                carriedForwardFilePaths = artifact.FinalResult.CarriedForwardFilePaths,
                carriedForwardCandidatesSkipped = artifact.FinalResult.CarriedForwardCandidatesSkipped,
            },
            stages = artifact.Stages.Select(stage => new
            {
                stageId = stage.StageId,
                label = stage.Label,
                relatedFilePath = stage.RelatedFilePath,
                outcome = stage.Outcome,
                iterationCount = stage.IterationCount,
                toolCallCount = stage.ToolCallCount,
                inputTokens = stage.InputTokens,
                outputTokens = stage.OutputTokens,
                finalConfidence = stage.FinalConfidence,
                modelId = stage.ModelId,
                connectionCategory = stage.ConnectionCategory?.ToString(),
                events = stage.Events.Select(@event => new
                {
                    kind = @event.Kind.ToString(),
                    name = @event.Name,
                    occurredAt = @event.OccurredAt,
                    inputTokens = @event.InputTokens,
                    outputTokens = @event.OutputTokens,
                    inputSample = @event.InputSample,
                    systemPrompt = @event.SystemPrompt,
                    outputSummary = @event.OutputSummary,
                    error = @event.Error,
                }),
            }),
            tokenUsage = new
            {
                totalInputTokens = artifact.TokenUsage.TotalInputTokens,
                totalOutputTokens = artifact.TokenUsage.TotalOutputTokens,
                byModel = artifact.TokenUsage.ByModel.Select(entry => new
                {
                    key = entry.Key,
                    totalInputTokens = entry.TotalInputTokens,
                    totalOutputTokens = entry.TotalOutputTokens,
                }),
                byConnectionCategory = artifact.TokenUsage.ByConnectionCategory.Select(entry => new
                {
                    key = entry.Key,
                    totalInputTokens = entry.TotalInputTokens,
                    totalOutputTokens = entry.TotalOutputTokens,
                }),
            },
            warnings = artifact.Warnings,
            verification = artifact.Verification is null
                ? null
                : new
                {
                    status = artifact.Verification.Status,
                    summary = artifact.Verification.Summary,
                    positiveExamples = artifact.Verification.PositiveExamples.Select(check => new
                    {
                        key = check.Key,
                        description = check.Description,
                        fulfilled = check.Fulfilled,
                        rationale = check.Rationale,
                    }),
                    negativeExamples = artifact.Verification.NegativeExamples.Select(check => new
                    {
                        key = check.Key,
                        description = check.Description,
                        fulfilled = check.Fulfilled,
                        rationale = check.Rationale,
                    }),
                    infoExamples = artifact.Verification.InfoExamples.Select(check => new
                    {
                        key = check.Key,
                        description = check.Description,
                        fulfilled = check.Fulfilled,
                        rationale = check.Rationale,
                    }),
                    notes = artifact.Verification.Notes,
                },
        };

        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        return outputPath;
    }
}
