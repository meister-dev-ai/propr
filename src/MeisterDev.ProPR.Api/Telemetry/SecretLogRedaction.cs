// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterDev.ProPR.Application.Telemetry;
using Serilog;

namespace MeisterDev.ProPR.Api.Telemetry;

/// <summary>
///     Registers the log-destructuring transforms that keep credentials out of the API host's log output.
/// </summary>
/// <remarks>
///     <para>
///         Serilog renders an object one of two ways. Interpolated into a message it uses <c>ToString</c>, which
///         the credential-bearing types override themselves. Destructured with <c>@</c> it reflects over the
///         properties instead and never consults <c>ToString</c>, and these transforms cover that second path.
///         Both have to be closed, because which one a call site used is not visible from the type.
///     </para>
///     <para>
///         The connection and endpoint types come from <see cref="AiConnectionLogRedaction" />, which every host
///         applies. This file adds the API host's own types on top: the request records its controllers bind,
///         which no other host has, and the inbound request.
///     </para>
///     <para>
///         It lives in this class instead of inline in the host so a test can apply the same policy and assert
///         on the output.
///     </para>
/// </remarks>
public static class SecretLogRedaction
{
    /// <summary>Applies every credential-scrubbing transform to <paramref name="configuration" />.</summary>
    /// <param name="configuration">The logger configuration to register the transforms on.</param>
    public static LoggerConfiguration Apply(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return AiConnectionLogRedaction.Apply(configuration)
            // Scrub secrets from log output: X-Ado-Token, X-User-Pat, AZURE_CLIENT_SECRET, AdoClientSecret
            .Destructure.ByTransforming<CreateAiConnectionRequest>(request => new
            {
                request.DisplayName,
                EndpointUrl = SecretSafeRendering.Address(request.EndpointUrl),
                request.Models,
                ApiKey = RedactSecret(request.ApiKey),
                request.ModelCapabilities,
                request.ModelCategory,
            })
            .Destructure.ByTransforming<UpdateAiConnectionRequest>(request => new
            {
                request.DisplayName,
                EndpointUrl = SecretSafeRendering.Address(request.EndpointUrl),
                request.Models,
                ApiKey = RedactSecret(request.ApiKey),
                request.ModelCapabilities,
            })
            .Destructure.ByTransforming<CreateClientProviderConnectionRequest>(request => new
            {
                request.ProviderFamily,
                HostBaseUrl = SecretSafeRendering.Address(request.HostBaseUrl),
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
                HostBaseUrl = SecretSafeRendering.Address(request.HostBaseUrl),
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
                EndpointUrl = SecretSafeRendering.Address(request.EndpointUrl),
                ApiKey = RedactSecret(request.ApiKey),
            })
            // The types above are request bodies. These two are the same credential further along, still inside the
            // API: the probe request and the auth block on its own. Each overrides ToString so plain interpolation
            // is safe; these entries cover the other rendering path, structured destructuring, where ToString is
            // not consulted at all.
            .Destructure.ByTransforming<ProbeAiConnectionRequest>(request => new
            {
                request.ProviderKind,
                BaseUrl = SecretSafeRendering.Address(request.BaseUrl),
                ApiKey = RedactSecret(request.Auth?.ApiKey),
            })
            .Destructure.ByTransforming<AiConnectionAuthRequest>(request => new
            {
                request.Mode,
                ApiKey = RedactSecret(request.ApiKey),
            })
            .Destructure.ByTransforming<TenantLocalLoginRequest>(request => new
            {
                request.Username,
                Password = RedactSecret(request.Password),
            })
            .Destructure.ByTransforming<HttpRequest>(r => new
            {
                r.Method,
                r.Path,
                HasProCursorSharedKey = r.Headers.ContainsKey(ProCursorSharedKeyAuthenticationDefaults.HeaderName),
            })
            // Last, so every transform above keeps its own projection and this covers what none of them names.
            .Apply(AiConnectionLogRedaction.ApplyCredentialFallback);
    }

    /// <summary>Threads a configuration through one more registration, so the chain above stays a chain.</summary>
    /// <param name="configuration">The configuration to pass on.</param>
    /// <param name="register">The registration to apply.</param>
    private static LoggerConfiguration Apply(
        this LoggerConfiguration configuration,
        Func<LoggerConfiguration, LoggerConfiguration> register)
    {
        return register(configuration);
    }

    private static string? RedactSecret(string? value)
    {
        return AiConnectionLogRedaction.Redact(value);
    }
}
