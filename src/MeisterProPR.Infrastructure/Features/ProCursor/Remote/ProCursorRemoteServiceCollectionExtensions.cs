// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.Infrastructure.Features.ProCursor.Remote;

/// <summary>
///     Registers the ProPR-side remote ProCursor transport and gateway selection.
/// </summary>
public static class ProCursorRemoteServiceCollectionExtensions
{
    public static IServiceCollection AddProCursorRemoteMode(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProCursorRemoteOptions>()
            .Configure(options =>
            {
                options.Mode = configuration["PROCURSOR_REMOTE_MODE"];
                options.ServiceBaseUrl = configuration["PROCURSOR_SERVICE_BASE_URL"];
                options.SharedKey = configuration["PROCURSOR_SHARED_KEY"];
                options.HealthEndpointPath = configuration["PROCURSOR_HEALTH_ENDPOINT"] ?? "/healthz";

                if (int.TryParse(configuration["PROCURSOR_REQUEST_TIMEOUT_SECONDS"], out var timeoutSeconds))
                {
                    options.RequestTimeoutSeconds = timeoutSeconds;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<HttpProCursorGateway>((sp, httpClient) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProCursorRemoteOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.ServiceBaseUrl))
            {
                httpClient.BaseAddress = new Uri(options.ServiceBaseUrl.TrimEnd('/') + "/");
            }

            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
            if (!string.IsNullOrWhiteSpace(options.SharedKey))
            {
                httpClient.DefaultRequestHeaders.Remove(ProCursorSharedKeyAuthenticationDefaults.HeaderName);
                httpClient.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, options.SharedKey);
            }
        });
        services.AddHttpClient<RemoteProCursorTokenUsageReadRepository>((sp, httpClient) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProCursorRemoteOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.ServiceBaseUrl))
            {
                httpClient.BaseAddress = new Uri(options.ServiceBaseUrl.TrimEnd('/') + "/");
            }

            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
            if (!string.IsNullOrWhiteSpace(options.SharedKey))
            {
                httpClient.DefaultRequestHeaders.Remove(ProCursorSharedKeyAuthenticationDefaults.HeaderName);
                httpClient.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, options.SharedKey);
            }
        });
        services.AddHttpClient<RemoteProCursorTokenUsageRebuildService>((sp, httpClient) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProCursorRemoteOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.ServiceBaseUrl))
            {
                httpClient.BaseAddress = new Uri(options.ServiceBaseUrl.TrimEnd('/') + "/");
            }

            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
            if (!string.IsNullOrWhiteSpace(options.SharedKey))
            {
                httpClient.DefaultRequestHeaders.Remove(ProCursorSharedKeyAuthenticationDefaults.HeaderName);
                httpClient.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, options.SharedKey);
            }
        });

        services.TryAddScoped<DisabledProCursorGateway>();

        return services;
    }
}
