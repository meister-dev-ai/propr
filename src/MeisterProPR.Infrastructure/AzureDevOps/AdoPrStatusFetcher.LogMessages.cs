using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed partial class AdoPrStatusFetcher
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to fetch ADO PR status for PR #{PullRequestId}; defaulting to Active (fail-safe)")]
    private static partial void LogStatusFetchFailed(ILogger logger, int pullRequestId, Exception ex);
}
