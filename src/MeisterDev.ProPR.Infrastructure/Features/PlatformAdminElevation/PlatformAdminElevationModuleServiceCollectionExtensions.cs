// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Application.Features.TenantAudit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Features.PlatformAdminElevation;

/// <summary>Registers the elevations of platform administrators and the audit history of tenants.</summary>
public static class PlatformAdminElevationModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the elevation service, the tenant audit history reader and the installation's maximum elevation
    ///     duration, read from <c>MEISTER_PLATFORM_ADMIN_ELEVATION_MAX_MINUTES</c>. A value that is not a whole number
    ///     of minutes from 1 to 1440 fails the start, so the installation never runs with another limit than the one
    ///     its operator set.
    /// </summary>
    public static IServiceCollection AddPlatformAdminElevationModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PlatformAdminElevationOptions>()
            .Configure(options =>
            {
                if (ReadMaxMinutes(configuration) is { } maxMinutes)
                {
                    options.MaxDurationMinutes = maxMinutes;
                }
            })
            .Validate(
                _ => string.IsNullOrWhiteSpace(configuration[PlatformAdminElevationOptions.MaxDurationMinutesKey])
                     || ReadMaxMinutes(configuration) is not null,
                $"{PlatformAdminElevationOptions.MaxDurationMinutesKey} must be a whole number of minutes from 1 to 1440.")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Registered whether or not a database is configured, like the tenant administration services, because test
        // hosts supply an in-memory context without a connection string.
        services.AddScoped<IPlatformAdminElevationService, PlatformAdminElevationService>();
        services.AddScoped<ITenantAuditHistoryReader, TenantAuditHistoryReader>();
        return services;
    }

    private static int? ReadMaxMinutes(IConfiguration configuration) =>
        int.TryParse(
            configuration[PlatformAdminElevationOptions.MaxDurationMinutesKey]?.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var maxMinutes)
            ? maxMinutes
            : null;
}
