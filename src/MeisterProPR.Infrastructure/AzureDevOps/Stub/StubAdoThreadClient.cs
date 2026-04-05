// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IAdoThreadClient" /> used when <c>ADO_STUB_PR=true</c>.
///     Thread status update calls are logged but no ADO API call is made.
/// </summary>
internal sealed partial class StubAdoThreadClient(ILogger<StubAdoThreadClient> logger) : IAdoThreadClient
{
    /// <inheritdoc />
    public Task UpdateThreadStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string status,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        LogStubCall(logger, pullRequestId, threadId, status);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "StubAdoThreadClient: skipped UpdateThreadStatus for PR#{PullRequestId} thread {ThreadId} → '{Status}' (ADO_STUB_PR=true)")]
    private static partial void LogStubCall(ILogger logger, int pullRequestId, int threadId, string status);
}
