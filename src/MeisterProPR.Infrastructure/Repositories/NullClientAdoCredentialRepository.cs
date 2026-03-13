using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     No-op implementation used in legacy (non-DB) mode.
///     Always returns <c>null</c>; upsert and clear are no-ops.
/// </summary>
public sealed class NullClientAdoCredentialRepository : IClientAdoCredentialRepository
{
    /// <inheritdoc />
    public Task<ClientAdoCredentials?> GetByClientIdAsync(Guid clientId, CancellationToken ct)
    {
        return Task.FromResult<ClientAdoCredentials?>(null);
    }

    /// <inheritdoc />
    public Task UpsertAsync(Guid clientId, ClientAdoCredentials credentials, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(Guid clientId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}