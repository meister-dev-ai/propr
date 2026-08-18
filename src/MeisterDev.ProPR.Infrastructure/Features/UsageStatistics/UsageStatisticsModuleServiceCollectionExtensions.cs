// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Http;
using MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageStatistics;

/// <summary>Registers the anonymous usage statistics sender and its administration services.</summary>
public static class UsageStatisticsModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Time a send is given before it is abandoned.
    ///     <para>
    ///         Kept short because the send runs on a background loop that must not compete with review work. A
    ///         snapshot that takes longer than this is dropped and the next cycle sends a fresh one.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Registers the module when database-backed runtime services are available.</summary>
    public static IServiceCollection AddUsageStatisticsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.HasDatabaseConnectionString())
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProductVersionProvider, AssemblyProductVersionProvider>();

        services.AddScoped<IUsageStatisticsStateStore, UsageStatisticsStateRepository>();
        services.AddScoped<IUsageStatisticsCountSource, UsageStatisticsCountRepository>();
        services.AddScoped<UsageStatisticsEditionResolver>();
        services.AddScoped<UsageStatisticsSnapshotBuilder>();
        services.AddScoped<UsageStatisticsService>();
        services.AddScoped<UsageStatisticsSender>();

        services.AddHttpClient<IUsageStatisticsPingClient, UsageStatisticsPingClient>((serviceProvider, client) =>
        {
            client.Timeout = SendTimeout;

            // A backstop under the client's own reader, which completes on headers and stops at the same
            // bound. Without this the default ceiling is 2 GB.
            client.MaxResponseContentBufferSize = UsageStatisticsPingClient.MaxResponseBytes;

            var version = serviceProvider.GetRequiredService<IProductVersionProvider>().Version;
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("propr", ToHeaderToken(version)));
        });

        return services;
    }

    /// <summary>
    ///     Reduces a version to characters an HTTP header may carry.
    ///     <para>
    ///         The version comes from a release tag, and a character outside the HTTP token set would throw
    ///         here, inside a factory delegate, on every cycle. The send loop catches that failure and logs it
    ///         at debug level, so the feature would stop working with little diagnostic output.
    ///     </para>
    /// </summary>
    private static string ToHeaderToken(string version)
    {
        const string allowedSymbols = "!#$%&'*+-.^_`|~";

        var token = new string([.. version.Where(character => char.IsAsciiLetterOrDigit(character) || allowedSymbols.Contains(character))]);

        return token.Length == 0 ? "unknown" : token;
    }
}
