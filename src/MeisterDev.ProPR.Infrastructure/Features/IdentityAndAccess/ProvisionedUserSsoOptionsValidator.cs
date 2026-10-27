// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     Checks the settings of the identity provider for provisioned users when the API starts. A wrong setting stops the
///     start, and the error message contains the name of each variable to correct.
/// </summary>
/// <param name="isDevelopment">
///     Whether the host runs in Development, where the provider may be reached over plain <c>http</c>.
/// </param>
public sealed class ProvisionedUserSsoOptionsValidator(bool isDevelopment) : IValidateOptions<ProvisionedUserSsoOptions>
{
    private static readonly InstallationSsoProviderKeys Keys = new(
        ProvisionedUserSsoOptions.AuthorityKey,
        ProvisionedUserSsoOptions.MetadataUrlKey,
        ProvisionedUserSsoOptions.ClientIdKey,
        ProvisionedUserSsoOptions.ClientSecretKey,
        ProvisionedUserSsoOptions.DisplayNameKey);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ProvisionedUserSsoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            return InstallationSsoProviderValidation.RefuseSettingsWithoutAuthority(
                ProvisionedUserSsoOptions.AuthorityKey,
                "let provisioned users sign in through the identity provider",
                [
                    (ProvisionedUserSsoOptions.MetadataUrlKey, options.MetadataUrl),
                    (ProvisionedUserSsoOptions.ClientIdKey, options.ClientId),
                    (ProvisionedUserSsoOptions.ClientSecretKey, options.ClientSecret),
                    (ProvisionedUserSsoOptions.DisplayNameKey, options.DisplayName),
                ]);
        }

        var failures = InstallationSsoProviderValidation.CheckProvider(options, Keys, isDevelopment);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
