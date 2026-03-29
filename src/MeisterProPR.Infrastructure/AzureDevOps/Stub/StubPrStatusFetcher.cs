using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IPrStatusFetcher" /> for dev/stub mode.
///     Always returns <see cref="PrStatus.Active" /> so that no jobs are incorrectly cancelled
///     when ADO is not connected.
/// </summary>
public sealed class StubPrStatusFetcher : IPrStatusFetcher
{
    /// <inheritdoc />
    public Task<PrStatus> GetStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId,
        CancellationToken ct = default)
    {
        return Task.FromResult(PrStatus.Active);
    }
}
