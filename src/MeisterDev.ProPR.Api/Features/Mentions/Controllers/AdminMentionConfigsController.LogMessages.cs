// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Controllers;

public sealed partial class AdminMentionConfigsController
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Mention configuration {ConfigId} created for client {ClientId} covering {RepositoryCount} repositories")]
    private static partial void LogMentionConfigCreated(
        ILogger logger,
        Guid configId,
        Guid clientId,
        int repositoryCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mention configuration {ConfigId} updated for client {ClientId}")]
    private static partial void LogMentionConfigUpdated(ILogger logger, Guid configId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mention configuration {ConfigId} deleted for client {ClientId}")]
    private static partial void LogMentionConfigDeleted(ILogger logger, Guid configId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mention configuration for client {ClientId} refused: the client already answers in that project")]
    private static partial void LogMentionConfigConflict(ILogger logger, Guid clientId);

    // The scope path itself stays out of the record. It is caller-supplied and the refusal is what matters;
    // the reason is enough to tell a misconfiguration from an attempt to name somewhere the client has no
    // connection to.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mention configuration for client {ClientId} on {Provider} refused: {Refusal}")]
    private static partial void LogMentionConfigScopeRefused(
        ILogger logger,
        Guid clientId,
        ScmProvider provider,
        MentionScopeRefusal refusal);
}
