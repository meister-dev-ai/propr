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
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlatformAdminSsoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (!options.IsConfigured)
        {
            var settingsWithoutAuthority = new (string Key, string? Value)[]
                {
                    (PlatformAdminSsoOptions.MetadataUrlKey, options.MetadataUrl),
                    (PlatformAdminSsoOptions.ClientIdKey, options.ClientId),
                    (PlatformAdminSsoOptions.ClientSecretKey, options.ClientSecret),
                    (PlatformAdminSsoOptions.RoleClaimKey, options.RoleClaim),
                    (PlatformAdminSsoOptions.RequiredRoleKey, options.RequiredRole),
                    (PlatformAdminSsoOptions.DisplayNameKey, options.DisplayName),
                }
                .Where(setting => !string.IsNullOrWhiteSpace(setting.Value))
                .Select(setting => setting.Key)
                .ToList();

            return settingsWithoutAuthority.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    $"{string.Join(", ", settingsWithoutAuthority)} {(settingsWithoutAuthority.Count == 1 ? "is" : "are")} set "
                    + $"without {PlatformAdminSsoOptions.AuthorityKey}. Set {PlatformAdminSsoOptions.AuthorityKey} to let "
                    + "platform administrators sign in through the identity provider, or remove the other settings.");
        }

        this.CheckAddress(failures, PlatformAdminSsoOptions.AuthorityKey, options.Authority, allowQuery: false);
        if (!string.IsNullOrWhiteSpace(options.MetadataUrl))
        {
            this.CheckAddress(failures, PlatformAdminSsoOptions.MetadataUrlKey, options.MetadataUrl, allowQuery: true);
        }

        RequireValue(failures, PlatformAdminSsoOptions.ClientIdKey, options.ClientId);
        RequireValue(failures, PlatformAdminSsoOptions.ClientSecretKey, options.ClientSecret);
        RequireValue(failures, PlatformAdminSsoOptions.RequiredRoleKey, options.RequiredRole);

        if (options.DisplayName is { } displayName
            && (displayName.Length > PlatformAdminSsoOptions.MaxDisplayNameLength || displayName.Any(char.IsControl)))
        {
            failures.Add(
                $"{PlatformAdminSsoOptions.DisplayNameKey} must be at most {PlatformAdminSsoOptions.MaxDisplayNameLength} "
                + "characters on one line.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireValue(List<string> failures, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} must be set when {PlatformAdminSsoOptions.AuthorityKey} is set.");
        }
    }

    private void CheckAddress(List<string> failures, string key, string? value, bool allowQuery)
    {
        var isAcceptable = Uri.TryCreate(value, UriKind.Absolute, out var address)
                           && (address.Scheme == Uri.UriSchemeHttps || (isDevelopment && address.Scheme == Uri.UriSchemeHttp))
                           && address.UserInfo.Length == 0
                           && (allowQuery || address.Query.Length == 0)
                           && address.Fragment.Length == 0;
        if (isAcceptable)
        {
            return;
        }

        var parts = allowQuery ? "credentials or fragment" : "credentials, query or fragment";
        failures.Add(
            isDevelopment
                ? $"{key} must be an absolute http or https URL without {parts}."
                : $"{key} must be an absolute https URL without {parts}. Plain http is accepted in Development only.");
    }
}
