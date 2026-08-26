// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.RemoveLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.UpdateLicensing;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicenseActivationHistory;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetSystemProfile;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing;

/// <summary>Registers installation licensing services.</summary>
public static class LicensingModuleServiceCollectionExtensions
{
    /// <summary>Registers the licensing module when database-backed runtime services are available.</summary>
    public static IServiceCollection AddLicensingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (!configuration.HasDatabaseConnectionString())
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IPremiumCapabilityCatalog, StaticPremiumCapabilityCatalog>();
        services.AddScoped<ILicensingPolicyStore, LicensingPolicyRepository>();
        services.AddScoped<ILicensingIdentityStore, LicensingIdentityRepository>();
        services.AddScoped<ILicensingCapabilityService, LicensingCapabilityService>();
        services.AddScoped<ILicensedResourceCountSource, LicensedResourceCountRepository>();

        // Records the day's highest concurrent-review count as work is claimed. Scoped because it writes
        // through the context the claim already holds, and registered here because only reporting reads it.
        services.AddScoped<IConcurrentReviewPeakStore, ConcurrentReviewPeakRepository>();

        // Resolves the effective ceiling for one quota. Scoped because it composes the capability service, which
        // reads the installation's capability overrides per request.
        services.AddScoped<ILicenseLimitResolver, LicenseLimitResolver>();

        // Decides whether one more client or runner fits. Scoped because it decides inside a transaction on the
        // scoped context injected into the request, which is the context the creation saves through.
        services.AddScoped<IStockQuotaGate, PostgresStockQuotaGate>();
        services.AddScoped<GetLicensingSummaryHandler>();
        services.AddScoped<UpdateLicensingHandler>();

        // The anchor is a singleton because it holds a certificate the verifier reads on every call, and the
        // verifier is a singleton because it holds nothing else and is safe to share.
        services.AddSingleton(_ => LicenseTrustAnchor.FromThisBuild());
        services.AddSingleton<LicenseVerifier>();
        services.AddScoped<IActivatedLicenseStore, InstallationLicenseRepository>();
        services.AddScoped<ILicenseActivationEventStore, LicenseActivationEventRepository>();

        // The clock is a singleton so that a host clock reading behind the recorded instant is reported once for
        // the process rather than once per reading.
        services.AddScoped<IHighestObservedTimeStore, InstallationObservedTimeRepository>();
        services.AddSingleton<ILicensingClock, RatchetedLicensingClock>();
        services.AddSingleton<ILicenseStateProvider, CachedLicenseStateProvider>();
        services.AddScoped<ActivateLicenseHandler>();
        services.AddScoped<RemoveLicenseHandler>();
        services.AddScoped<GetLicenseActivationHistoryHandler>();

        // The observed system profile. Nothing in verification, resolution, activation or quota enforcement
        // reads it.
        services.AddSingleton<ISystemProfileEnvironmentProbe, RuntimeSystemProfileEnvironmentProbe>();
        services.AddScoped<IDatabaseClusterIdentityProbe, PostgresClusterIdentityProbe>();
        services.AddScoped<IConfiguredScmHostSource, ConfiguredScmHostRepository>();
        services.AddScoped<ISystemProfileStore, SystemProfileRepository>();
        services.AddScoped<ISystemProfileObserver, SystemProfileObserver>();
        services.AddScoped<GetSystemProfileHandler>();

        // The month-and-author rollup. Written where reviews and mention answers complete and read by the
        // surfaces that report consumption, so it is scoped to the same request as those writes.
        services.AddScoped<IAuthorActivityRollupStore, AuthorActivityRollupRepository>();

        // The exclusion decision and the read it needs. The recorder is what the completion paths call, so the
        // decision is made once and the store only keeps the flag it was given.
        services.AddScoped<IConfiguredReviewerIdentitySource, ConfiguredReviewerIdentityRepository>();
        services.AddScoped<IAuthorActivityRecorder, AuthorActivityRecorder>();

        // The recorded author-allowance overage and the comparison that records it. Nothing in intake, claiming
        // or dispatch resolves either, and none of those points resolves the author dimension from the limit
        // resolver, so the allowance is recorded and reported rather than enforced.
        services.AddScoped<IAuthorOverageStore, AuthorOverageRepository>();
        services.AddScoped<IAuthorOverageEvaluator, AuthorOverageEvaluator>();

        return services;
    }
}
