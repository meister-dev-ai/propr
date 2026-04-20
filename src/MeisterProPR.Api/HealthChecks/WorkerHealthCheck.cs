// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MeisterProPR.Api.HealthChecks;

/// <summary>
///     Health check that reports whether the background review worker is running.
/// </summary>
public sealed class WorkerHealthCheck(
    ReviewJobWorker worker,
    IServiceProvider serviceProvider,
    IConfiguration configuration) : IHealthCheck
{
    /// <summary>Checks worker health and returns a HealthCheckResult.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var databaseConfigured = configuration.HasDatabaseConnectionString();
        var providerRegistry = serviceProvider.GetService<IScmProviderRegistry>();
        var readinessProfileCatalog = serviceProvider.GetService<IProviderReadinessProfileCatalog>();
        var providerActivationService = serviceProvider.GetService<IProviderActivationService>();
        var activationStatuses = databaseConfigured && providerActivationService is not null
            ? (await providerActivationService.ListAsync(cancellationToken))
            .ToDictionary(status => status.ProviderFamily)
            : null;
        var providerStatuses = Enum.GetValues<ScmProvider>()
            .Select(provider =>
            {
                ProviderActivationStatusDto? activationStatus = null;
                if (activationStatuses is not null)
                {
                    activationStatuses.TryGetValue(provider, out activationStatus);
                }

                var baselineAdapterSetRegistered =
                    databaseConfigured && providerRegistry?.IsRegistered(provider) == true;

                return new ProviderRegistrySnapshot(
                    GetProviderKey(provider),
                    baselineAdapterSetRegistered,
                    activationStatus?.IsEnabled ?? true,
                    baselineAdapterSetRegistered && (activationStatus?.IsEnabled ?? true),
                    databaseConfigured && providerRegistry is not null
                        ? providerRegistry.GetRegisteredCapabilities(provider)
                        : [],
                    activationStatus?.SupportClaimReadiness ??
                    ResolveSupportClaimReadiness(readinessProfileCatalog, provider),
                    activationStatus?.SupportClaimReason ??
                    ResolveSupportClaimReason(readinessProfileCatalog, provider),
                    activationStatus?.UpdatedAt);
            })
            .ToArray();

        var data = new Dictionary<string, object>
        {
            ["reviewJobWorkerRunning"] = worker.IsRunning,
            ["databaseConfigured"] = databaseConfigured,
            ["providerRegistryAvailable"] = providerRegistry is not null,
            ["providerActivationAvailable"] = providerActivationService is not null,
            ["providerRegistrationSemantics"] = "baselineAdapterSet",
            ["providerActivationSemantics"] = "installationWideAdminPolicy",
            ["providerReadinessSemantics"] = "leastReadyHostVariantSupportClaim",
        };

        foreach (var providerStatus in providerStatuses)
        {
            data[$"provider.{providerStatus.Key}.registered"] = providerStatus.BaselineAdapterSetRegistered;
            data[$"provider.{providerStatus.Key}.enabled"] = providerStatus.IsEnabled;
            data[$"provider.{providerStatus.Key}.effectiveAvailable"] = providerStatus.EffectiveAvailable;
            data[$"provider.{providerStatus.Key}.registeredCapabilities"] = providerStatus.RegisteredCapabilities;
            data[$"provider.{providerStatus.Key}.supportClaimReadiness"] =
                ToReadinessValue(providerStatus.SupportClaimReadiness);
            data[$"provider.{providerStatus.Key}.supportClaimReason"] = providerStatus.SupportClaimReason;
            if (providerStatus.ActivationUpdatedAt is not null)
            {
                data[$"provider.{providerStatus.Key}.activationUpdatedAt"] =
                    providerStatus.ActivationUpdatedAt.Value.ToString("O");
            }
        }

        if (!worker.IsRunning)
        {
            return HealthCheckResult.Unhealthy("Worker is not running.", data: data);
        }

        if (!databaseConfigured)
        {
            return HealthCheckResult.Healthy(
                "Worker is running. SCM provider registry is inactive because no database is configured.",
                data);
        }

        if (providerRegistry is null)
        {
            return HealthCheckResult.Degraded(
                "Worker is running, but the SCM provider registry is unavailable.",
                data: data);
        }

        var missingProviders = providerStatuses
            .Where(entry => !entry.BaselineAdapterSetRegistered)
            .Select(entry => entry.Key)
            .ToArray();

        if (missingProviders.Length > 0)
        {
            data["missingProviders"] = missingProviders;
            return HealthCheckResult.Degraded(
                "Worker is running, but one or more SCM provider baseline adapter sets are not registered.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            "Worker is running and SCM provider baseline adapter sets are registered.",
            data);
    }

    private static string GetProviderKey(ScmProvider provider)
    {
        return provider switch
        {
            ScmProvider.AzureDevOps => "azureDevOps",
            ScmProvider.GitHub => "github",
            ScmProvider.GitLab => "gitLab",
            ScmProvider.Forgejo => "forgejo",
            _ => provider.ToString(),
        };
    }

    private static ProviderConnectionReadinessLevel ResolveSupportClaimReadiness(
        IProviderReadinessProfileCatalog? readinessProfileCatalog,
        ScmProvider provider)
    {
        var profiles = readinessProfileCatalog?.GetProfiles(provider) ?? [];
        if (profiles.Count == 0)
        {
            return ProviderConnectionReadinessLevel.Unknown;
        }

        var leastReadyProfile = profiles
            .OrderBy(profile => GetReadinessOrder(
                profile.IsWorkflowComplete
                    ? ProviderConnectionReadinessLevel.WorkflowComplete
                    : ProviderConnectionReadinessLevel.OnboardingReady))
            .First();

        return leastReadyProfile.IsWorkflowComplete
            ? ProviderConnectionReadinessLevel.WorkflowComplete
            : ProviderConnectionReadinessLevel.OnboardingReady;
    }

    private static string ResolveSupportClaimReason(
        IProviderReadinessProfileCatalog? readinessProfileCatalog,
        ScmProvider provider)
    {
        var profiles = readinessProfileCatalog?.GetProfiles(provider) ?? [];
        if (profiles.Count == 0)
        {
            return "No readiness profile is registered for this provider family.";
        }

        var leastReadyProfile = profiles
            .OrderBy(profile => GetReadinessOrder(
                profile.IsWorkflowComplete
                    ? ProviderConnectionReadinessLevel.WorkflowComplete
                    : ProviderConnectionReadinessLevel.OnboardingReady))
            .First();

        return leastReadyProfile.Notes;
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

    private static string ToReadinessValue(ProviderConnectionReadinessLevel level)
    {
        return level switch
        {
            ProviderConnectionReadinessLevel.WorkflowComplete => "workflowComplete",
            ProviderConnectionReadinessLevel.OnboardingReady => "onboardingReady",
            ProviderConnectionReadinessLevel.Configured => "configured",
            ProviderConnectionReadinessLevel.Degraded => "degraded",
            _ => "unknown",
        };
    }

    private sealed record ProviderRegistrySnapshot(
        string Key,
        bool BaselineAdapterSetRegistered,
        bool IsEnabled,
        bool EffectiveAvailable,
        IReadOnlyList<string> RegisteredCapabilities,
        ProviderConnectionReadinessLevel SupportClaimReadiness,
        string SupportClaimReason,
        DateTimeOffset? ActivationUpdatedAt);
}
