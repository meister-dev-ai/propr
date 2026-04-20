// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Controllers;

public sealed partial class AdminCrawlConfigsController
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Crawl config {ConfigId} created for client {ClientId} by admin")]
    private static partial void LogCrawlConfigCreated(ILogger logger, Guid configId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Crawl config {ConfigId} deleted by admin")]
    private static partial void LogCrawlConfigDeleted(ILogger logger, Guid configId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Guided crawl configuration create failed for client {ClientId}, provider project {ProjectId}, organization scope {OrganizationScopeId}: {Reason}")]
    private static partial void LogGuidedCrawlConfigCreateValidationFailed(
        ILogger logger,
        Guid clientId,
        string projectId,
        Guid? organizationScopeId,
        string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Guided crawl configuration update failed for config {ConfigId}: {Reason}")]
    private static partial void LogGuidedCrawlConfigPatchValidationFailed(ILogger logger, Guid configId, string reason);
}
