// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.HealthChecks;

/// <summary>
///     Checks the extracted ProCursor dependency when ProPR is running in remote mode.
/// </summary>
public sealed class RemoteProCursorHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<ProCursorRemoteOptions> options,
    ILogger<RemoteProCursorHealthCheck> logger) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var remote = options.Value;
        if (!remote.IsRemoteEnabled)
        {
            return HealthCheckResult.Healthy("Remote ProCursor mode is disabled.");
        }

        var requestUri = (remote.HealthEndpointPath ?? "/healthz").Trim();
        try
        {
            var client = httpClientFactory.CreateClient(nameof(RemoteProCursorHealthCheck));
            client.BaseAddress = new Uri(remote.ServiceBaseUrl!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, remote.RequestTimeoutSeconds));
            client.DefaultRequestHeaders.Remove(ProCursorSharedKeyAuthenticationDefaults.HeaderName);
            client.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, remote.SharedKey);

            using var response = await client.GetAsync(requestUri.TrimStart('/'), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Remote ProCursor dependency is reachable.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return HealthCheckResult.Unhealthy("Remote ProCursor rejected the shared key.");
            }

            return HealthCheckResult.Degraded($"Remote ProCursor returned status {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(
                "Remote ProCursor health probe failed for {ServiceBaseUrl}{HealthEndpointPath}: {ErrorMessage}",
                remote.ServiceBaseUrl,
                requestUri,
                ex.Message);

            return HealthCheckResult.Unhealthy("Remote ProCursor is unavailable.");
        }
    }
}
