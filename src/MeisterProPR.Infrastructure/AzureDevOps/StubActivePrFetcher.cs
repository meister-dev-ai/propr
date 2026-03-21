using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     No-op implementation of <see cref="IActivePrFetcher" /> used when <c>ADO_STUB_PR=true</c>.
///     Always returns an empty list so the scan worker runs without hitting ADO.
/// </summary>
internal sealed partial class StubActivePrFetcher(ILogger<StubActivePrFetcher> logger) : IActivePrFetcher
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ActivePullRequestRef>> GetRecentlyUpdatedPullRequestsAsync(
        string organizationUrl,
        string projectId,
        DateTimeOffset updatedAfter,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        LogStubCall(logger, organizationUrl, projectId);
        return Task.FromResult<IReadOnlyList<ActivePullRequestRef>>([]);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "StubActivePrFetcher: returning empty PR list for {OrganizationUrl}/{ProjectId}")]
    private static partial void LogStubCall(ILogger logger, string organizationUrl, string projectId);
}
