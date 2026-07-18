// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;

internal static class ProRVFocusedReviewGuidanceResolver
{
    private const int MaxGuidanceItems = 4;

    public static async Task<ProRVFocusedReviewGuidanceResolution> TryResolveAsync(
        ReviewJob job,
        ChangedFile file,
        IChatClient chatClient,
        string? modelId,
        Guid? protocolId,
        IProtocolRecorder protocolRecorder,
        IProRVPrefilter? proRvPrefilter,
        ILogger logger,
        string stageId,
        string runtimeSource,
        CancellationToken ct)
    {
        var recording = new ProtocolRecordingHandle(protocolId, protocolRecorder);

        if (proRvPrefilter is null ||
            file.IsBinary ||
            file.ChangeType == ChangeType.Delete ||
            string.IsNullOrWhiteSpace(file.UnifiedDiff))
        {
            var skipReason = ResolveSkipReason(proRvPrefilter, file);
            await RecordStageEventAsync(
                recording,
                ReviewProtocolEventNames.ProRVPrefilterSkipped,
                new ProRVStageLocation(file.Path, stageId),
                new { reason = skipReason },
                null,
                null,
                ct);
            return ProRVFocusedReviewGuidanceResolution.Skipped(skipReason);
        }

        try
        {
            await RecordStageEventAsync(
                recording,
                ReviewProtocolEventNames.ProRVPrefilterStarted,
                new ProRVStageLocation(file.Path, stageId),
                null,
                null,
                null,
                ct);

            var request = new ProRVPrefilterRequest(file.Path, file.UnifiedDiff)
            {
                Language = TryResolveExplicitLanguage(file.Path),
                MaxResults = MaxGuidanceItems,
            };

            var result = await proRvPrefilter.RankRelevantItemsAsync(
                request,
                chatClient,
                new ChatOptions { ModelId = modelId },
                ct);

            await RecordProtocolUsageAsync(
                recording,
                file.Path,
                stageId,
                runtimeSource,
                modelId,
                result,
                ct);

            var guidance = result.Status != ProRVPrefilterStatus.Success || result.Items.Count == 0
                ? []
                : result.Items
                    .Select(item => new FocusedReviewGuidanceItem(
                        item.Id,
                        item.Title,
                        item.ShortDescription,
                        item.Instruction,
                        item.Reason,
                        item.Score))
                    .ToList()
                    .AsReadOnly();

            var lifecycleEvent = result.Status == ProRVPrefilterStatus.Success
                ? ReviewProtocolEventNames.ProRVPrefilterCompleted
                : ReviewProtocolEventNames.ProRVPrefilterFailed;
            await RecordStageEventAsync(
                recording,
                lifecycleEvent,
                new ProRVStageLocation(file.Path, stageId),
                new
                {
                    runtimeSource,
                    modelId,
                    proRvStatus = result.Status.ToString(),
                    guidanceCount = guidance.Count,
                    language = result.Language,
                },
                result.Status == ProRVPrefilterStatus.Success ? null : result.FailureReason,
                null,
                ct);

            return new ProRVFocusedReviewGuidanceResolution(
                result.Status == ProRVPrefilterStatus.Success ? ProRVStageExecutionStates.Completed : ProRVStageExecutionStates.Failed,
                result.Status == ProRVPrefilterStatus.Success ? null : result.FailureReason,
                result.Status,
                runtimeSource,
                modelId,
                result.Language,
                guidance);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProRV prefilter failed for file {FilePath} in job {JobId}", file.Path, job.Id);
            await RecordStageEventAsync(
                recording,
                ReviewProtocolEventNames.ProRVPrefilterFailed,
                new ProRVStageLocation(file.Path, stageId),
                null,
                ex.Message,
                null,
                ct);
            return ProRVFocusedReviewGuidanceResolution.Failed(ex.Message);
        }
    }

    private static async Task RecordProtocolUsageAsync(
        ProtocolRecordingHandle recording,
        string filePath,
        string stageId,
        string runtimeSource,
        string? modelId,
        ProRVPrefilterResult result,
        CancellationToken ct)
    {
        if (!recording.ProtocolId.HasValue)
        {
            return;
        }

        await recording.Recorder.RecordAiCallAsync(
            recording.ProtocolId.Value,
            0,
            result.InputTokens,
            result.OutputTokens,
            JsonSerializer.Serialize(
                new
                {
                    purpose = AiPurpose.ProRVPrefilter.ToString(), filePath, stageId, status = result.Status.ToString(), language = result.Language,
                    runtimeSource,
                }),
            JsonSerializer.Serialize(new { purpose = AiPurpose.ProRVPrefilter.ToString(), stageId, modelId, runtimeSource }),
            result.RawResponse,
            ct,
            ReviewProtocolEventNames.ProRVPrefilterAiCall,
            result.Status == ProRVPrefilterStatus.Success ? null : result.FailureReason,
            cachedInputTokens: result.CachedInputTokens,
            cacheWriteTokens: result.CacheWriteTokens,
            reasoningTokens: result.ReasoningTokens);

        if (result.InputTokens.GetValueOrDefault() > 0 || result.OutputTokens.GetValueOrDefault() > 0)
        {
            await recording.Recorder.AddTokensAsync(
                recording.ProtocolId.Value,
                result.InputTokens.GetValueOrDefault(),
                result.OutputTokens.GetValueOrDefault(),
                modelId: modelId,
                ct: ct,
                cachedInputTokens: result.CachedInputTokens.GetValueOrDefault(),
                cacheWriteTokens: result.CacheWriteTokens.GetValueOrDefault(),
                reasoningTokens: result.ReasoningTokens.GetValueOrDefault());
        }
    }

    private static async Task RecordStageEventAsync(
        ProtocolRecordingHandle recording,
        string eventName,
        ProRVStageLocation location,
        object? output,
        string? error,
        object? extraDetails,
        CancellationToken ct)
    {
        if (!recording.ProtocolId.HasValue)
        {
            return;
        }

        var details = JsonSerializer.Serialize(
            new
            {
                purpose = AiPurpose.ProRVPrefilter.ToString(),
                filePath = location.FilePath,
                stageId = location.StageId,
                details = extraDetails,
            });

        await recording.Recorder.RecordProRvEventAsync(
            recording.ProtocolId.Value,
            eventName,
            details,
            output is null ? null : JsonSerializer.Serialize(output),
            error,
            ct);
    }

    private static string ResolveSkipReason(IProRVPrefilter? proRvPrefilter, ChangedFile file)
    {
        if (proRvPrefilter is null)
        {
            return ProRVStageSkipReasons.NotConfigured;
        }

        if (file.IsBinary)
        {
            return ProRVStageSkipReasons.BinaryFile;
        }

        return file.ChangeType == ChangeType.Delete
            ? ProRVStageSkipReasons.DeletedFile
            : ProRVStageSkipReasons.EmptyDiff;
    }

    private static string? TryResolveExplicitLanguage(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".csx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cshtml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".razor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".config", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase))
        {
            return "csharp";
        }

        if (string.Equals(extension, ".js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".mjs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cjs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".tsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".mts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return "javascript";
        }

        if (string.Equals(extension, ".c", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cpp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cxx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".hh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".hpp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".hxx", StringComparison.OrdinalIgnoreCase))
        {
            return "cpp";
        }

        if (string.Equals(extension, ".go", StringComparison.OrdinalIgnoreCase))
        {
            return "go";
        }

        if (string.Equals(extension, ".java", StringComparison.OrdinalIgnoreCase))
        {
            return "java";
        }

        if (string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase))
        {
            return "python";
        }

        if (string.Equals(extension, ".rb", StringComparison.OrdinalIgnoreCase))
        {
            return "ruby";
        }

        if (string.Equals(extension, ".rs", StringComparison.OrdinalIgnoreCase))
        {
            return "rust";
        }

        if (string.Equals(extension, ".swift", StringComparison.OrdinalIgnoreCase))
        {
            return "swift";
        }

        return filePath.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase))
            ? "actions"
            : null;
    }

    internal sealed record ProRVFocusedReviewGuidanceResolution(
        string ExecutionState,
        string? Reason,
        ProRVPrefilterStatus? PrefilterStatus,
        string? RuntimeSource,
        string? ModelId,
        string? Language,
        IReadOnlyList<FocusedReviewGuidanceItem> Guidance)
    {
        public static ProRVFocusedReviewGuidanceResolution Skipped(string reason)
        {
            return new ProRVFocusedReviewGuidanceResolution(ProRVStageExecutionStates.Skipped, reason, null, null, null, null, []);
        }

        public static ProRVFocusedReviewGuidanceResolution Failed(string reason)
        {
            return new ProRVFocusedReviewGuidanceResolution(ProRVStageExecutionStates.Failed, reason, null, null, null, null, []);
        }
    }

    internal static class ProRVStageExecutionStates
    {
        public const string NotSelected = "not_selected";
        public const string Skipped = "skipped";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }

    internal static class ProRVStageSkipReasons
    {
        public const string NotConfigured = "not_configured";
        public const string BinaryFile = "binary_file";
        public const string DeletedFile = "deleted_file";
        public const string EmptyDiff = "empty_diff";
    }

    internal static class ProRVRuntimeSources
    {
        // The ProRV-lens applicability ranking runs on the lens pass entry's own configured model.
        public const string LensEntryModel = "lens_entry_model";
    }

    private readonly record struct ProtocolRecordingHandle(Guid? ProtocolId, IProtocolRecorder Recorder);

    private readonly record struct ProRVStageLocation(string FilePath, string StageId);
}
