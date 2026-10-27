// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     Writes at startup whether platform administrators can sign in through the configured identity provider. A
///     provider that the license does not cover is ignored, and the log of the start is where an operator finds the
///     reason the sign-in page shows the local form only.
/// </summary>
public static partial class PlatformAdminSsoStartupDiagnostics
{
    /// <summary>Writes the diagnostic when a provider is configured; writes nothing otherwise.</summary>
    /// <param name="services">A service scope of the started host.</param>
    /// <param name="logger">The logger the diagnostic is written to.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public static async Task ReportAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = services.GetRequiredService<IOptions<PlatformAdminSsoOptions>>().Value;
        if (!settings.IsConfigured)
        {
            return;
        }

        // The validation at startup refuses an authority with credentials, a query or a fragment, so the address is
        // written as configured.
        var authority = settings.Authority!;
        var licensing = services.GetService<ILicensingCapabilityService>();
        bool isLicensed;
        try
        {
            isLicensed = licensing is not null && await licensing.IsEnabledAsync(PremiumCapabilityKey.SsoAuthentication, ct);
        }
        // The diagnostic must not stop a start that would otherwise succeed. A license that cannot be read here is
        // read again at every sign-in.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogLicenseUnreadable(logger, exception, PlatformAdminSsoOptions.AuthorityKey);
            return;
        }

        if (isLicensed)
        {
            LogProviderInUse(logger, authority, settings.EffectiveRoleClaim, settings.RequiredRole);
        }
        else
        {
            LogProviderIgnored(logger, PlatformAdminSsoOptions.AuthorityKey, PremiumCapabilityKey.SsoAuthentication);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Platform administrators can sign in through the identity provider at {Authority}. An account receives "
                  + "platform administrator rights while the claim {RoleClaim} of its ID token contains {RequiredRole}.")]
    private static partial void LogProviderInUse(ILogger logger, string authority, string roleClaim, string? requiredRole);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{AuthorityKey} is set, but the license of this installation does not grant {Capability}. ProPR ignores "
                  + "the identity provider for platform administrators, and the sign-in page offers the local sign-in only.")]
    private static partial void LogProviderIgnored(ILogger logger, string authorityKey, string capability);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{AuthorityKey} is set, but the license could not be read at startup. ProPR checks the license again at "
                  + "every sign-in through the identity provider for platform administrators.")]
    private static partial void LogLicenseUnreadable(ILogger logger, Exception exception, string authorityKey);
}
