// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persists which repositories each client answers `@`-mentions on.
/// </summary>
public interface IMentionConfigurationRepository
{
    /// <summary>Creates a configuration together with the repositories it answers on.</summary>
    /// <param name="clientId">The client that answers.</param>
    /// <param name="provider">Normalized source-control provider family.</param>
    /// <param name="providerScopePath">Provider scope path the project lives under.</param>
    /// <param name="providerProjectKey">Provider project, workspace, or namespace key.</param>
    /// <param name="scanIntervalSeconds">Shortest gap between two scans.</param>
    /// <param name="repoFilters">The repositories answered on. Must not be empty.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MentionConfigurationDto> AddAsync(
        Guid clientId,
        ScmProvider provider,
        string providerScopePath,
        string providerProjectKey,
        int scanIntervalSeconds,
        IReadOnlyList<MentionRepoFilterDto> repoFilters,
        CancellationToken ct = default);

    /// <summary>Returns every configuration the scan should visit, newest last.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MentionConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default);

    /// <summary>
    ///     Returns every configuration, active or not, for an administrator's view across all clients.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="GetAllActiveAsync" /> on purpose. Reusing the scan's query for a listing
    ///     hides paused configurations from the only screen that could reactivate them, and the uniqueness
    ///     rule then refuses to create a replacement for one the operator cannot see.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MentionConfigurationDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns one client's configurations, active or not.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MentionConfigurationDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns one configuration, or null when no such configuration exists.</summary>
    /// <param name="configId">The configuration identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MentionConfigurationDto?> GetByIdAsync(Guid configId, CancellationToken ct = default);

    /// <summary>
    ///     Replaces a configuration's scan interval, active flag and repository list.
    /// </summary>
    /// <param name="configId">The configuration identifier.</param>
    /// <param name="clientId">The owning client, so one client cannot edit another's configuration.</param>
    /// <param name="scanIntervalSeconds">New scan interval, or null to leave it.</param>
    /// <param name="isActive">New active flag, or null to leave it.</param>
    /// <param name="repoFilters">Replacement repository list, or null to leave it. Must not be empty when given.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> UpdateAsync(
        Guid configId,
        Guid clientId,
        int? scanIntervalSeconds,
        bool? isActive,
        IReadOnlyList<MentionRepoFilterDto>? repoFilters,
        CancellationToken ct = default);

    /// <summary>Deletes a configuration and the repositories it listed.</summary>
    /// <param name="configId">The configuration identifier.</param>
    /// <param name="clientId">The owning client.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default);
}
