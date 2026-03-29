using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Repository for per-client AI reviewer finding dismissals.</summary>
public interface IFindingDismissalRepository
{
    /// <summary>Returns all dismissals for the given client, ordered by <c>CreatedAt</c> descending.</summary>
    Task<IReadOnlyList<FindingDismissal>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns the dismissal with the given ID, or <see langword="null" /> if not found.</summary>
    Task<FindingDismissal?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Persists a new dismissal.</summary>
    Task AddAsync(FindingDismissal dismissal, CancellationToken ct = default);

    /// <summary>Persists changes to an existing dismissal (e.g. updated label).</summary>
    Task UpdateAsync(FindingDismissal dismissal, CancellationToken ct = default);

    /// <summary>Removes the dismissal with the given ID. Returns <see langword="false" /> if not found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
