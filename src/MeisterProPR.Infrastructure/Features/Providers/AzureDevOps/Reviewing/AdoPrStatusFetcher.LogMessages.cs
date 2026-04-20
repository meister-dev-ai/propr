// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

public sealed partial class AdoPrStatusFetcher
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to fetch ADO PR status for PR #{PullRequestId}; defaulting to Active (fail-safe)")]
    private static partial void LogStatusFetchFailed(ILogger logger, int pullRequestId, Exception ex);
}
