// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Clients.Services;

/// <summary>Builds provider operational status views from evaluated readiness.</summary>
public sealed class ProviderOperationalStatusService(
    IClientScmConnectionRepository connectionRepository,
    IProviderReadinessEvaluator readinessEvaluator,
    IScmProviderRegistry providerRegistry) : IProviderOperationalStatusService
{
    /// <summary>Gets the provider operational status for a client.</summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The provider operational status data transfer object.</returns>
    public async Task<ProviderOperationalStatusDto> GetForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var connections = await connectionRepository.GetByClientIdAsync(clientId, ct);
        var connectionResults = new List<ProviderConnectionOperationalStatusDto>(connections.Count);

        foreach (var connection in connections.OrderBy(entry => entry.ProviderFamily)
                     .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var readiness = await readinessEvaluator.EvaluateAsync(clientId, connection, ct);
            connectionResults.Add(
                new ProviderConnectionOperationalStatusDto(
                    connection.Id,
                    connection.ProviderFamily,
                    connection.DisplayName,
                    connection.HostBaseUrl,
                    readiness.HostVariant,
                    connection.IsActive,
                    connection.VerificationStatus,
                    readiness.ReadinessLevel,
                    readiness.ReadinessReason,
                    readiness.MissingCriteria,
                    ResolveHealth(connection, readiness.ReadinessLevel),
                    connection.LastVerifiedAt,
                    ResolveFailureCategory(connection, readiness.ReadinessLevel),
                    ResolveStatusReason(connection, readiness.ReadinessReason)));
        }

        var providerFamilies = connectionResults
            .GroupBy(entry => entry.ProviderFamily)
            .Select(group => BuildFamilySummary(group.Key, group.ToList(), providerRegistry.IsRegistered(group.Key)))
            .OrderBy(entry => entry.ProviderFamily)
            .ToList()
            .AsReadOnly();

        return new ProviderOperationalStatusDto(connectionResults.AsReadOnly(), providerFamilies);
    }

    private static ProviderFamilyOperationalStatusDto BuildFamilySummary(
        ScmProvider providerFamily,
        IReadOnlyList<ProviderConnectionOperationalStatusDto> connections,
        bool baselineAdapterSetRegistered)
    {
        var hostVariants = connections
            .GroupBy(entry => entry.HostVariant)
            .Select(group => BuildHostVariantSummary(group.Key, group.ToList()))
            .OrderBy(entry => entry.HostVariant, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

        var leastReady = connections
            .OrderBy(entry => GetReadinessOrder(entry.ReadinessLevel))
            .FirstOrDefault();

        return new ProviderFamilyOperationalStatusDto(
            providerFamily,
            baselineAdapterSetRegistered,
            leastReady?.ReadinessLevel ?? ProviderConnectionReadinessLevel.Unknown,
            leastReady?.ReadinessReason ?? "No provider connections configured for this provider family.",
            Count(connections, ProviderConnectionReadinessLevel.Unknown),
            Count(connections, ProviderConnectionReadinessLevel.Configured),
            Count(connections, ProviderConnectionReadinessLevel.OnboardingReady),
            Count(connections, ProviderConnectionReadinessLevel.WorkflowComplete),
            Count(connections, ProviderConnectionReadinessLevel.Degraded),
            hostVariants);
    }

    private static ProviderHostVariantOperationalStatusDto BuildHostVariantSummary(
        string hostVariant,
        IReadOnlyList<ProviderConnectionOperationalStatusDto> connections)
    {
        var leastReady = connections
            .OrderBy(entry => GetReadinessOrder(entry.ReadinessLevel))
            .First();

        return new ProviderHostVariantOperationalStatusDto(
            hostVariant,
            leastReady.ReadinessLevel,
            leastReady.ReadinessReason,
            Count(connections, ProviderConnectionReadinessLevel.Unknown),
            Count(connections, ProviderConnectionReadinessLevel.Configured),
            Count(connections, ProviderConnectionReadinessLevel.OnboardingReady),
            Count(connections, ProviderConnectionReadinessLevel.WorkflowComplete),
            Count(connections, ProviderConnectionReadinessLevel.Degraded));
    }

    private static int Count(
        IEnumerable<ProviderConnectionOperationalStatusDto> connections,
        ProviderConnectionReadinessLevel level)
    {
        return connections.Count(entry => entry.ReadinessLevel == level);
    }

    private static int GetReadinessOrder(ProviderConnectionReadinessLevel level)
    {
        return level switch
        {
            ProviderConnectionReadinessLevel.WorkflowComplete => 4,
            ProviderConnectionReadinessLevel.OnboardingReady => 3,
            ProviderConnectionReadinessLevel.Configured => 2,
            ProviderConnectionReadinessLevel.Degraded => 1,
            _ => 0,
        };
    }

    private static string ResolveHealth(
        ClientScmConnectionDto connection,
        ProviderConnectionReadinessLevel readinessLevel)
    {
        if (!connection.IsActive)
        {
            return "inactive";
        }

        return readinessLevel switch
        {
            ProviderConnectionReadinessLevel.WorkflowComplete => "healthy",
            ProviderConnectionReadinessLevel.Degraded => "failing",
            _ when string.Equals(
                connection.VerificationStatus,
                "failed",
                StringComparison.OrdinalIgnoreCase) => "failing",
            _ => "degraded",
        };
    }

    private static string ResolveStatusReason(ClientScmConnectionDto connection, string readinessReason)
    {
        if (!connection.IsActive)
        {
            return "Connection is disabled.";
        }

        return string.IsNullOrWhiteSpace(connection.LastVerificationError)
            ? readinessReason
            : connection.LastVerificationError;
    }

    private static string? ResolveFailureCategory(
        ClientScmConnectionDto connection,
        ProviderConnectionReadinessLevel readinessLevel)
    {
        if (!string.IsNullOrWhiteSpace(connection.LastVerificationFailureCategory))
        {
            return connection.LastVerificationFailureCategory;
        }

        return readinessLevel switch
        {
            ProviderConnectionReadinessLevel.Degraded when !connection.IsActive => "configuration",
            _ => null,
        };
    }
}
