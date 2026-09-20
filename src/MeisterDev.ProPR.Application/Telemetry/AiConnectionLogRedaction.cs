// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Application.DTOs;
using Serilog;

namespace MeisterDev.ProPR.Application.Telemetry;

/// <summary>
///     Registers the log-destructuring transforms that keep an AI connection's credential out of log output.
/// </summary>
/// <remarks>
///     <para>
///         Serilog renders an object one of two ways. Interpolated into a message it uses <c>ToString</c>, which
///         these types override themselves; destructured with <c>@</c> it reflects over the properties instead
///         and never consults <c>ToString</c>, which these transforms cover. Both paths have to be
///         closed, because which one a call site used is not visible from the type.
///     </para>
///     <para>
///         Each transform names the members it projects, so a member added to one of these types is absent from
///         log output until someone lists it. <c>ProviderSettings</c> and <c>DeclaredSecrets</c> are therefore
///         never written: their contents are chosen by a provider family, and the host has no basis for judging
///         which entry is safe to publish. The base URL is projected through
///         <see cref="SecretSafeRendering.Address" /> for the same reason: userinfo and a query parameter are
///         both places an operator can put a key, and several providers expect one in the second.
///     </para>
///     <para>
///         It lives in this assembly because both hosts handle these four types. The runner relays every model
///         call, holds provider endpoints, and cannot reference the API, so a policy that stayed in the API host
///         would have to be copied there, and a copy drifts.
///     </para>
/// </remarks>
public static class AiConnectionLogRedaction
{
    /// <summary>Applies every credential-scrubbing transform to <paramref name="configuration" />.</summary>
    /// <param name="configuration">The logger configuration to register the transforms on.</param>
    public static LoggerConfiguration Apply(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The same credential at four points on its way through a host: the profile as the application sees it,
        // the write request the repository persists, the probe options a driver is handed, and the endpoint a
        // driver reaches the provider with.
        return configuration
            .Destructure.ByTransforming<AiConnectionDto>(connection => new
            {
                connection.Id,
                connection.DisplayName,
                connection.ProviderKind,
                BaseUrl = SecretSafeRendering.Address(connection.BaseUrl),
                connection.AuthMode,
                connection.ClientId,
                connection.TenantId,
                connection.IsActive,
                Secret = Redact(connection.Secret),
            })
            .Destructure.ByTransforming<AiConnectionWriteRequestDto>(request => new
            {
                request.DisplayName,
                request.ProviderKind,
                BaseUrl = SecretSafeRendering.Address(request.BaseUrl),
                request.AuthMode,
                request.DiscoveryMode,
                Secret = Redact(request.Secret),
            })
            .Destructure.ByTransforming<AiConnectionProbeOptionsDto>(options => new
            {
                options.ProviderKind,
                BaseUrl = SecretSafeRendering.Address(options.BaseUrl),
                options.AuthMode,
                Secret = Redact(options.Secret),
            })
            .Destructure.ByTransforming<ProviderEndpoint>(endpoint => new
            {
                endpoint.ProviderKind,
                BaseUrl = SecretSafeRendering.Address(endpoint.BaseUrl),
                endpoint.AuthMode,
                Secret = Redact(endpoint.Secret),
            });
    }

    /// <summary>
    ///     Registers the floor under the named transforms: any object holding a member named the way credential
    ///     material is named has that member elided.
    /// </summary>
    /// <remarks>
    ///     Registered last by each host, after every transform of its own, so a type that has one keeps it. It
    ///     covers the types nobody listed, which is the case a list cannot cover.
    /// </remarks>
    /// <param name="configuration">The logger configuration to register the policy on.</param>
    public static LoggerConfiguration ApplyCredentialFallback(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.Destructure.With(new CredentialMemberDestructuringPolicy());
    }

    /// <summary>Replaces a credential with its presence, keeping the distinction between absent and set.</summary>
    /// <param name="value">The credential material, which is never included in the result.</param>
    public static string? Redact(string? value)
    {
        // Empty means absent. Whitespace means a credential was set to whitespace, which is still something
        // the operator stored and not something to print back.
        return string.IsNullOrEmpty(value) ? value : "[REDACTED]";
    }
}
