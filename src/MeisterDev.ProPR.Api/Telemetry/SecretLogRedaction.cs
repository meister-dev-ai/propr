// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterDev.ProPR.Application.DTOs;
using Serilog;

namespace MeisterDev.ProPR.Api.Telemetry;

/// <summary>
///     Registers the log-destructuring transforms that keep credentials out of log output.
/// </summary>
/// <remarks>
///     <para>
///         Serilog renders an object one of two ways. Interpolated into a message it uses <c>ToString</c>, which
///         the credential-bearing types override themselves; destructured with <c>@</c> it reflects over the
///         properties instead and never consults <c>ToString</c>, which is what these transforms cover. Both paths
///         have to be closed, because which one a call site used is not visible from the type.
///     </para>
///     <para>
///         It lives here rather than inline in the host so the policy can be exercised by a test. A redaction rule
///         nobody can run is a rule nobody knows is still working.
///     </para>
/// </remarks>
public static class SecretLogRedaction
{
    /// <summary>Applies every credential-scrubbing transform to <paramref name="configuration" />.</summary>
    /// <param name="configuration">The logger configuration to register the transforms on.</param>
    public static LoggerConfiguration Apply(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration
            // Scrub secrets from log output: X-Ado-Token, X-User-Pat, AZURE_CLIENT_SECRET, AdoClientSecret
            .Destructure.ByTransforming<CreateAiConnectionRequest>(request => new
            {
                request.DisplayName,
                request.EndpointUrl,
                request.Models,
                ApiKey = RedactSecret(request.ApiKey),
                request.ModelCapabilities,
                request.ModelCategory,
            })
            .Destructure.ByTransforming<UpdateAiConnectionRequest>(request => new
            {
                request.DisplayName,
                request.EndpointUrl,
                request.Models,
                ApiKey = RedactSecret(request.ApiKey),
                request.ModelCapabilities,
            })
            .Destructure.ByTransforming<CreateClientProviderConnectionRequest>(request => new
            {
                request.ProviderFamily,
                request.HostBaseUrl,
                request.AuthenticationKind,
                request.UserName,
                request.OAuthTenantId,
                request.OAuthClientId,
                request.DisplayName,
                Secret = RedactSecret(request.Secret),
                request.IsActive,
            })
            .Destructure.ByTransforming<PatchClientProviderConnectionRequest>(request => new
            {
                request.HostBaseUrl,
                request.AuthenticationKind,
                request.UserName,
                request.OAuthTenantId,
                request.OAuthClientId,
                request.DisplayName,
                Secret = RedactSecret(request.Secret),
                request.IsActive,
            })
            .Destructure.ByTransforming<DiscoverModelsRequest>(request => new
            {
                request.EndpointUrl,
                ApiKey = RedactSecret(request.ApiKey),
            })
            // The types above are request bodies. These four are the same credential further along: the auth block on
            // its own, the profile as the application sees it, the write request the repository persists, and the
            // probe options a driver is handed. Each overrides ToString so plain interpolation is safe; these entries
            // cover the other rendering path, structured destructuring, where ToString is not consulted at all.
            .Destructure.ByTransforming<ProbeAiConnectionRequest>(request => new
            {
                request.ProviderKind,
                request.BaseUrl,
                ApiKey = RedactSecret(request.Auth?.ApiKey),
            })
            .Destructure.ByTransforming<AiConnectionAuthRequest>(request => new
            {
                request.Mode,
                ApiKey = RedactSecret(request.ApiKey),
            })
            .Destructure.ByTransforming<AiConnectionDto>(connection => new
            {
                connection.Id,
                connection.DisplayName,
                connection.ProviderKind,
                connection.BaseUrl,
                connection.AuthMode,
                connection.ClientId,
                connection.TenantId,
                connection.IsActive,
                Secret = RedactSecret(connection.Secret),
            })
            .Destructure.ByTransforming<AiConnectionWriteRequestDto>(request => new
            {
                request.DisplayName,
                request.ProviderKind,
                request.BaseUrl,
                request.AuthMode,
                request.DiscoveryMode,
                Secret = RedactSecret(request.Secret),
            })
            .Destructure.ByTransforming<AiConnectionProbeOptionsDto>(options => new
            {
                options.ProviderKind,
                options.BaseUrl,
                options.AuthMode,
                Secret = RedactSecret(options.Secret),
            })
            .Destructure.ByTransforming<ProviderEndpoint>(endpoint => new
            {
                endpoint.ProviderKind,
                endpoint.BaseUrl,
                endpoint.AuthMode,
                Secret = RedactSecret(endpoint.Secret),
            })
            .Destructure.ByTransforming<HttpRequest>(r => new
            {
                r.Method,
                r.Path,
                HasProCursorSharedKey = r.Headers.ContainsKey(ProCursorSharedKeyAuthenticationDefaults.HeaderName),
            });
    }

    private static string? RedactSecret(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : "[REDACTED]";
    }
}
