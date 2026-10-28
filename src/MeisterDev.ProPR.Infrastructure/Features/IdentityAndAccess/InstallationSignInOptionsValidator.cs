// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     Checks the sign-in settings of the installation when the API starts. A value other than <c>true</c> or
///     <c>false</c> stops the start, because a typing error in a setting that turns a sign-in off must not leave the
///     sign-in on. An installation that turns off the password sign-in and configures no identity provider for platform
///     administrators does not start either, because no administrator could sign in to it.
/// </summary>
/// <remarks>
///     The license decides whether the identity provider for platform administrators is in use, and ProPR reads the
///     license from the database. That part of the check runs after the migrations, in
///     <c>InstallationSsoStartupDiagnostics</c>.
/// </remarks>
/// <param name="configuration">The configuration the settings are read from.</param>
public sealed class InstallationSignInOptionsValidator(IConfiguration configuration) : IValidateOptions<InstallationSignInOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, InstallationSignInOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        foreach (var key in new[] { InstallationSignInOptions.LocalSignInEnabledKey, InstallationSignInOptions.TenantSsoEnabledKey })
        {
            if (IsInvalidSwitch(configuration, key))
            {
                failures.Add($"{key} must be true or false.");
            }
        }

        if (!options.LocalSignInEnabled && string.IsNullOrWhiteSpace(configuration[PlatformAdminSsoOptions.AuthorityKey]))
        {
            failures.Add(
                $"{InstallationSignInOptions.LocalSignInEnabledKey} is false, and {PlatformAdminSsoOptions.AuthorityKey} is not set. "
                + "Without the password sign-in, platform administrators sign in only through the identity provider for "
                + "platform administrators, so no administrator could sign in. Configure that provider with the "
                + $"MEISTER_PLATFORM_ADMIN_SSO_* variables, or set {InstallationSignInOptions.LocalSignInEnabledKey} to true.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Reads a setting that turns a sign-in on or off.</summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="key">The variable of the setting.</param>
    /// <returns>
    ///     The value of the setting, or <see langword="null" /> when the setting is unset, blank, or neither <c>true</c>
    ///     nor <c>false</c>. Upper and lower case do not matter.
    /// </returns>
    internal static bool? ReadSwitch(IConfiguration configuration, string key)
    {
        return bool.TryParse(configuration[key]?.Trim(), out var value) ? value : null;
    }

    private static bool IsInvalidSwitch(IConfiguration configuration, string key)
    {
        return !string.IsNullOrWhiteSpace(configuration[key]) && ReadSwitch(configuration, key) is null;
    }
}
