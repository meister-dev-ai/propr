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
///     it. An installation that turns off the password sign-in does not start without a licensed provider for platform
///     administrators, because no administrator could sign in to it.
/// </summary>
public static partial class InstallationSsoStartupDiagnostics
{
    /// <summary>Writes one diagnostic for each configured provider; writes nothing when no provider is configured.</summary>
    /// <param name="services">A service scope of the started host.</param>
    /// <param name="logger">The logger the diagnostics are written to.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <exception cref="OptionsValidationException">
    ///     The installation turns off the password sign-in, and the license does not grant single sign-on or could not be
    ///     read. The host logs the message, which contains the names of the variables, and ends the start.
    /// </exception>
    public static async Task ReportAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        // Reading the settings runs their validator, which refuses an installation without the password sign-in that
        // configures no provider for platform administrators.
        var signIns = services.GetRequiredService<IOptions<InstallationSignInOptions>>().Value;
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
        // A license that cannot be read here is read again at every sign-in, so the diagnostics do not stop the start for
        // it. An installation without the password sign-in is the exception, because its administrators sign in only
        // through the licensed provider.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogLicenseUnreadable(logger, exception);
            if (!signIns.LocalSignInEnabled)
            {
                throw RefuseStart(
                    $"{InstallationSignInOptions.LocalSignInEnabledKey} is false, and the license of this installation could "
                    + $"not be read at startup. ProPR cannot tell whether the license grants {PremiumCapabilityKey.SsoAuthentication}, "
                    + $"which the identity provider for platform administrators ({PlatformAdminSsoOptions.AuthorityKey}) needs; "
                    + "without it, no administrator could sign in. Correct the license, or set "
                    + $"{InstallationSignInOptions.LocalSignInEnabledKey} to true.");
            }

            return;
        }

        if (!signIns.LocalSignInEnabled && !isLicensed)
        {
            throw RefuseStart(
                $"{InstallationSignInOptions.LocalSignInEnabledKey} is false, and the license of this installation does not "
                + $"grant {PremiumCapabilityKey.SsoAuthentication}. Without it, ProPR ignores the identity provider for platform "
                + $"administrators ({PlatformAdminSsoOptions.AuthorityKey}), so no administrator could sign in. Activate a "
                + $"license that grants single sign-on, or set {InstallationSignInOptions.LocalSignInEnabledKey} to true.");
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

    // The host logs an OptionsValidationException as a fatal error and ends the start with exit code 1, as for a setting
    // that its validator refuses.
    private static OptionsValidationException RefuseStart(string failure)
    {
        return new OptionsValidationException(Options.DefaultName, typeof(InstallationSignInOptions), [failure]);
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
