// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     Writes at startup whether people can sign in through the identity providers that the installation configures: the
///     provider for platform administrators and the provider for provisioned users. A provider that the license does not
///     cover is ignored, and the log of the start is where an operator finds the reason the sign-in page does not offer
///     it.
/// </summary>
public static partial class InstallationSsoStartupDiagnostics
{
    /// <summary>Writes one diagnostic for each configured provider; writes nothing when no provider is configured.</summary>
    /// <param name="services">A service scope of the started host.</param>
    /// <param name="logger">The logger the diagnostics are written to.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public static async Task ReportAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var platformAdmin = services.GetRequiredService<IOptions<PlatformAdminSsoOptions>>().Value;
        var provisionedUser = services.GetRequiredService<IOptions<ProvisionedUserSsoOptions>>().Value;
        if (!platformAdmin.IsConfigured && !provisionedUser.IsConfigured)
        {
            return;
        }

        var licensing = services.GetService<ILicensingCapabilityService>();
        bool isLicensed;
        try
        {
            isLicensed = licensing is not null && await licensing.IsEnabledAsync(PremiumCapabilityKey.SsoAuthentication, ct);
        }
        // The diagnostics must not stop a start that would otherwise succeed. A license that cannot be read here is read
        // again at every sign-in.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogLicenseUnreadable(logger, exception);
            return;
        }

        // The validation at startup refuses an authority with credentials, a query or a fragment, so the addresses are
        // written as configured.
        if (platformAdmin.IsConfigured)
        {
            if (isLicensed)
            {
                LogPlatformAdminProviderInUse(logger, platformAdmin.Authority!, platformAdmin.EffectiveRoleClaim, platformAdmin.RequiredRole);
            }
            else
            {
                LogPlatformAdminProviderIgnored(logger, PlatformAdminSsoOptions.AuthorityKey, PremiumCapabilityKey.SsoAuthentication);
            }
        }

        if (provisionedUser.IsConfigured)
        {
            if (isLicensed)
            {
                LogProvisionedUserProviderInUse(logger, provisionedUser.Authority!);
            }
            else
            {
                LogProvisionedUserProviderIgnored(logger, ProvisionedUserSsoOptions.AuthorityKey, PremiumCapabilityKey.SsoAuthentication);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Platform administrators can sign in through the identity provider at {Authority}. An account receives "
                  + "platform administrator rights while the claim {RoleClaim} of its ID token contains {RequiredRole}.")]
    private static partial void LogPlatformAdminProviderInUse(ILogger logger, string authority, string roleClaim, string? requiredRole);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{AuthorityKey} is set, but the license of this installation does not grant {Capability}. ProPR ignores "
                  + "the identity provider for platform administrators, and the sign-in page offers the local sign-in only.")]
    private static partial void LogPlatformAdminProviderIgnored(ILogger logger, string authorityKey, string capability);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provisioned users can sign in through the identity provider at {Authority}. The sign-in reaches only the "
                  + "accounts that a hosting platform provisioned through the administration API.")]
    private static partial void LogProvisionedUserProviderInUse(ILogger logger, string authority);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{AuthorityKey} is set, but the license of this installation does not grant {Capability}. ProPR ignores "
                  + "the identity provider for provisioned users, and the sign-in page does not offer it.")]
    private static partial void LogProvisionedUserProviderIgnored(ILogger logger, string authorityKey, string capability);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "An identity provider of the installation is configured, but the license could not be read at startup. "
                  + "ProPR checks the license again at every sign-in through the provider.")]
    private static partial void LogLicenseUnreadable(ILogger logger, Exception exception);
}
