// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

public partial class AdoRepositoryMaterializer
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Materialized ProCursor repository source {SourceId} branch {BranchName} at commit {CommitSha} with {FileCount} files.")]
    private static partial void LogMaterializedRepositorySource(ILogger logger, Guid sourceId, string branchName, string commitSha, int fileCount);
}
