// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Clients.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Clients.Support;

/// <summary>Database-backed installation-wide provider activation policy service.</summary>
public sealed class ProviderActivationService(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory,
    IServiceProvider serviceProvider,
    IProviderReadinessProfileCatalog readinessProfileCatalog) : IProviderActivationService
{
    public async Task<IReadOnlyList<ProviderActivationStatusDto>> ListAsync(CancellationToken ct = default)
    {
        var records = await this.EnsureDefaultsAsync(ct, false);

        return Enum.GetValues<ScmProvider>()
            .Select(provider => this.BuildStatus(GetExistingOrDefaultRecord(records, provider)))
            .ToList()
            .AsReadOnly();
    }

    public async Task<ProviderActivationStatusDto> SetEnabledAsync(
        ScmProvider provider,
        bool isEnabled,
        CancellationToken ct = default)
    {
        var records = await this.EnsureDefaultsAsync(ct, true);
        var record = records[provider];

        if (record.IsEnabled != isEnabled)
        {
            record.IsEnabled = isEnabled;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(ct);
        }

        return this.BuildStatus(record);
    }

    public async Task<bool> IsEnabledAsync(ScmProvider provider, CancellationToken ct = default)
    {
        var records = await this.EnsureDefaultsAsync(ct, false);
        return records.TryGetValue(provider, out var record)
            ? record.IsEnabled
            : GetDefaultEnabled(provider);
    }

    public async Task<IReadOnlySet<ScmProvider>> GetEnabledProvidersAsync(CancellationToken ct = default)
    {
        var records = await this.EnsureDefaultsAsync(ct, false);
        var enabledProviders = Enum.GetValues<ScmProvider>()
            .Where(provider => records.TryGetValue(provider, out var record)
                ? record.IsEnabled
                : GetDefaultEnabled(provider))
            .ToHashSet();

        return enabledProviders;
    }

    private async Task<Dictionary<ScmProvider, ProviderActivationRecord>> EnsureDefaultsAsync(CancellationToken ct, bool allowWrites)
    {
        if (!allowWrites && contextFactory is not null)
        {
            await using var readDb = await contextFactory.CreateDbContextAsync(ct);
            var readRecords = await readDb.ProviderActivations
                .AsNoTracking()
                .ToDictionaryAsync(record => record.Provider, ct);

            foreach (var provider in Enum.GetValues<ScmProvider>())
            {
                if (!readRecords.ContainsKey(provider))
                {
                    readRecords.Add(provider, CreateDefaultRecord(provider));
                }
            }

            return readRecords;
        }

        var records = await dbContext.ProviderActivations
            .ToDictionaryAsync(record => record.Provider, ct);

        var missingProviders = Enum.GetValues<ScmProvider>()
            .Where(provider => !records.ContainsKey(provider))
            .ToList();

        if (missingProviders.Count == 0)
        {
            return records;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var provider in missingProviders)
        {
            var record = new ProviderActivationRecord
            {
                Provider = provider,
                IsEnabled = GetDefaultEnabled(provider),
                UpdatedAt = now,
            };

            dbContext.ProviderActivations.Add(record);
            records.Add(provider, record);
        }

        await dbContext.SaveChangesAsync(ct);
        return records;
    }

    private ProviderActivationStatusDto BuildStatus(ProviderActivationRecord record)
    {
        var profiles = readinessProfileCatalog.GetProfiles(record.Provider);
        var providerRegistry = serviceProvider.GetRequiredService<IScmProviderRegistry>();

        return new ProviderActivationStatusDto(
            record.Provider,
            record.IsEnabled,
            providerRegistry.IsRegistered(record.Provider),
            providerRegistry.GetRegisteredCapabilities(record.Provider),
            ResolveSupportClaimReadiness(profiles),
            ResolveSupportClaimReason(profiles),
            record.UpdatedAt);
    }

    private static bool GetDefaultEnabled(ScmProvider provider)
    {
        return provider is ScmProvider.AzureDevOps or ScmProvider.GitLab;
    }

    private static ProviderActivationRecord GetExistingOrDefaultRecord(
        IReadOnlyDictionary<ScmProvider, ProviderActivationRecord> records,
        ScmProvider provider)
    {
        return records.TryGetValue(provider, out var record)
            ? record
            : CreateDefaultRecord(provider);
    }

    private static ProviderActivationRecord CreateDefaultRecord(ScmProvider provider)
    {
        return new ProviderActivationRecord
        {
            Provider = provider,
            IsEnabled = GetDefaultEnabled(provider),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProviderConnectionReadinessLevel ResolveSupportClaimReadiness(IReadOnlyList<ProviderReadinessProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return ProviderConnectionReadinessLevel.Unknown;
        }

        return profiles.All(profile => profile.IsWorkflowComplete)
            ? ProviderConnectionReadinessLevel.WorkflowComplete
            : ProviderConnectionReadinessLevel.OnboardingReady;
    }

    private static string ResolveSupportClaimReason(IReadOnlyList<ProviderReadinessProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return "No readiness profile is registered for this provider family.";
        }

        return profiles
            .OrderBy(profile => profile.IsWorkflowComplete ? 1 : 0)
            .ThenBy(profile => profile.HostVariant, StringComparer.Ordinal)
            .First()
            .Notes;
    }
}
