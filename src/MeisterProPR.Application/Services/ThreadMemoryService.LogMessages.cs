// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class ThreadMemoryService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Memory embedding stored for thread {ThreadId} client {ClientId}")]
    private static partial void LogEmbeddingStored(ILogger logger, int threadId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory embedding {Outcome} for thread {ThreadId} client {ClientId}")]
    private static partial void LogEmbeddingRemoved(ILogger logger, int threadId, Guid clientId, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to process resolved thread {ThreadId} for client {ClientId}")]
    private static partial void LogProcessResolvedFailedCore(ILogger logger, Exception exception, int threadId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to process reopened thread {ThreadId} for client {ClientId}")]
    private static partial void LogProcessReopenedFailedCore(ILogger logger, Exception exception, int threadId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to record no-op for thread {ThreadId} client {ClientId}")]
    private static partial void LogRecordNoOpFailedCore(ILogger logger, Exception exception, int threadId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to retrieve and reconsider for file {FilePath} client {ClientId}")]
    private static partial void LogRetrieveAndReconsiderFailedCore(ILogger logger, Exception exception, string filePath, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory reconsideration AI call failed; returning draft result unchanged")]
    private static partial void LogReconsiderationAiFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory reconsideration returned no usable result for file {FilePath} client {ClientId}; draft findings retained unchanged")]
    private static partial void LogReconsiderationFallbackCore(ILogger logger, string filePath, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Historical duplicate suppression embedding lookup failed for file {FilePath} client {ClientId}")]
    private static partial void LogDuplicateSuppressionEmbeddingFailedCore(ILogger logger, Exception exception, string filePath, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Historical duplicate suppression repository lookup failed for repo {RepositoryId} PR {PullRequestId} client {ClientId}")]
    private static partial void LogDuplicateSuppressionLookupFailedCore(ILogger logger, Exception exception, string repositoryId, int pullRequestId, Guid clientId);

    private static void LogProcessResolvedFailed(ILogger logger, int threadId, Guid clientId, Exception exception) =>
        LogProcessResolvedFailedCore(logger, exception, threadId, clientId);

    private static void LogProcessReopenedFailed(ILogger logger, int threadId, Guid clientId, Exception exception) =>
        LogProcessReopenedFailedCore(logger, exception, threadId, clientId);

    private static void LogRecordNoOpFailed(ILogger logger, int threadId, Guid clientId, Exception exception) =>
        LogRecordNoOpFailedCore(logger, exception, threadId, clientId);

    private static void LogRetrieveAndReconsiderFailed(ILogger logger, string filePath, Guid clientId, Exception exception) =>
        LogRetrieveAndReconsiderFailedCore(logger, exception, SanitizeForLog(filePath), clientId);

    private static void LogReconsiderationFallback(ILogger logger, string filePath, Guid clientId) =>
        LogReconsiderationFallbackCore(logger, SanitizeForLog(filePath), clientId);

    private static void LogDuplicateSuppressionEmbeddingFailed(ILogger logger, string? filePath, Guid clientId, Exception exception) =>
        LogDuplicateSuppressionEmbeddingFailedCore(logger, exception, SanitizeForLog(filePath), clientId);

    private static void LogDuplicateSuppressionLookupFailed(ILogger logger, string repositoryId, int pullRequestId, Guid clientId, Exception exception) =>
        LogDuplicateSuppressionLookupFailedCore(logger, exception, SanitizeForLog(repositoryId), pullRequestId, clientId);

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\r' => ' ',
                '\n' => ' ',
                '\t' => ' ',
                _ when char.IsControl(character) => '?',
                _ => character,
            });
        }

        return builder.ToString();
    }
}
