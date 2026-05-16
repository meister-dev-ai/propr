// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed partial class FileByFileReviewOrchestrator
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting synthesis for job {JobId}")]
    private static partial void LogSynthesisStarted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed synthesis for job {JobId}")]
    private static partial void LogSynthesisCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed review for file {FilePath} in job {JobId}")]
    private static partial void LogFileReviewFailed(ILogger logger, string filePath, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Synthesis failed for job {JobId} — using fallback concatenation")]
    private static partial void LogSynthesisFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Synthesis for job {JobId} returned invalid JSON — requesting one repair pass")]
    private static partial void LogSynthesisJsonRepairStarted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synthesis JSON repair succeeded for job {JobId}")]
    private static partial void LogSynthesisJsonRepairSucceeded(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Synthesis JSON repair failed for job {JobId} — using fallback concatenation")]
    private static partial void LogSynthesisJsonRepairFailed(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to begin protocol recording for file {FilePath} in job {JobId}")]
    private static partial void LogProtocolBeginFailed(ILogger logger, string filePath, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Excluded file {FilePath} from review in job {JobId} (matched pattern: {Pattern})")]
    private static partial void LogFileExcluded(ILogger logger, string filePath, string pattern, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{CrossCuttingCount} cross-cutting concern(s) identified in synthesis for job {JobId}")]
    private static partial void LogCrossCuttingConcernsFound(ILogger logger, int crossCuttingCount, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Quality filter started for job {JobId}: {CommentCount} comments before filter")]
    private static partial void LogQualityFilterStarted(ILogger logger, Guid jobId, int commentCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Quality filter completed for job {JobId}: {Before} → {After} comments")]
    private static partial void LogQualityFilterCompleted(ILogger logger, Guid jobId, int before, int after);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Quality filter failed for job {JobId} — using pre-filter comment list")]
    private static partial void LogQualityFilterFailed(ILogger logger, Guid jobId, Exception ex);
}
