// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     Checks the settings of the identity provider for platform administrators when the API starts. A wrong
///     setting stops the start, and the error message contains the name of each variable to correct, because a
///     provider that fails only at the first sign-in is found by the person who needs to sign in.
/// </summary>
/// <param name="isDevelopment">
///     Whether the host runs in Development, where the provider may be reached over plain <c>http</c>.
/// </param>
public sealed class PlatformAdminSsoOptionsValidator(bool isDevelopment) : IValidateOptions<PlatformAdminSsoOptions>
{
    private static readonly InstallationSsoProviderKeys Keys = new(
        PlatformAdminSsoOptions.AuthorityKey,
        PlatformAdminSsoOptions.MetadataUrlKey,
        PlatformAdminSsoOptions.ClientIdKey,
        PlatformAdminSsoOptions.ClientSecretKey,
        PlatformAdminSsoOptions.DisplayNameKey);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlatformAdminSsoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            return InstallationSsoProviderValidation.RefuseSettingsWithoutAuthority(
                PlatformAdminSsoOptions.AuthorityKey,
                "let platform administrators sign in through the identity provider",
                [
                    (PlatformAdminSsoOptions.MetadataUrlKey, options.MetadataUrl),
                    (PlatformAdminSsoOptions.ClientIdKey, options.ClientId),
                    (PlatformAdminSsoOptions.ClientSecretKey, options.ClientSecret),
                    (PlatformAdminSsoOptions.RoleClaimKey, options.RoleClaim),
                    (PlatformAdminSsoOptions.RequiredRoleKey, options.RequiredRole),
                    (PlatformAdminSsoOptions.DisplayNameKey, options.DisplayName),
                ]);
        }

        var failures = InstallationSsoProviderValidation.CheckProvider(options, Keys, isDevelopment);
        InstallationSsoProviderValidation.RequireValue(
            failures,
            PlatformAdminSsoOptions.RequiredRoleKey,
            options.RequiredRole,
            PlatformAdminSsoOptions.AuthorityKey);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
