// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     The checks that the settings of every identity provider of the installation pass when the API starts. Each
///     message contains the name of the variable to correct.
/// </summary>
internal static class InstallationSsoProviderValidation
{
    /// <summary>
    ///     Returns success when the authority and every other setting of a provider are unset, and a failure that names
    ///     the settings that are set without the authority otherwise.
    /// </summary>
    /// <param name="authorityKey">The variable of the authority.</param>
    /// <param name="purpose">What the authority turns on, for example <c>let provisioned users sign in through the identity provider</c>.</param>
    /// <param name="settings">The other variables of the provider with their values.</param>
    public static ValidateOptionsResult RefuseSettingsWithoutAuthority(
        string authorityKey,
        string purpose,
        IEnumerable<(string Key, string? Value)> settings)
    {
        var settingsWithoutAuthority = settings
            .Where(setting => !string.IsNullOrWhiteSpace(setting.Value))
            .Select(setting => setting.Key)
            .ToList();

        return settingsWithoutAuthority.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{string.Join(", ", settingsWithoutAuthority)} {(settingsWithoutAuthority.Count == 1 ? "is" : "are")} set "
                + $"without {authorityKey}. Set {authorityKey} to {purpose}, or remove the other settings.");
    }

    /// <summary>
    ///     Checks the settings that every configured provider has: the authority and the metadata address, the client
    ///     and the display name.
    /// </summary>
    /// <param name="options">The settings of a provider whose authority is set.</param>
    /// <param name="keys">The variables of the provider.</param>
    /// <param name="isDevelopment">Whether the host runs in Development, where the provider may be reached over plain <c>http</c>.</param>
    /// <returns>The failures; an empty list when every setting is acceptable.</returns>
    public static List<string> CheckProvider(
        InstallationSsoProviderOptions options,
        InstallationSsoProviderKeys keys,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);

        var failures = new List<string>();
        CheckAddress(failures, keys.AuthorityKey, options.Authority, allowQuery: false, isDevelopment);
        if (!string.IsNullOrWhiteSpace(options.MetadataUrl))
        {
            CheckAddress(failures, keys.MetadataUrlKey, options.MetadataUrl, allowQuery: true, isDevelopment);
        }

        RequireValue(failures, keys.ClientIdKey, options.ClientId, keys.AuthorityKey);
        RequireValue(failures, keys.ClientSecretKey, options.ClientSecret, keys.AuthorityKey);

        if (options.DisplayName is { } displayName
            && (displayName.Length > InstallationSsoProviderOptions.MaxDisplayNameLength || displayName.Any(char.IsControl)))
        {
            failures.Add($"{keys.DisplayNameKey} must be at most {InstallationSsoProviderOptions.MaxDisplayNameLength} characters on one line.");
        }

        return failures;
    }

    /// <summary>Adds a failure when <paramref name="value" /> is not set.</summary>
    /// <param name="failures">The failures found so far.</param>
    /// <param name="key">The variable that must be set.</param>
    /// <param name="value">Its value.</param>
    /// <param name="authorityKey">The variable of the authority, which makes the setting required.</param>
    public static void RequireValue(List<string> failures, string key, string? value, string authorityKey)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} must be set when {authorityKey} is set.");
        }
    }

    private static void CheckAddress(List<string> failures, string key, string? value, bool allowQuery, bool isDevelopment)
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

/// <summary>The variables that configure an identity provider of the installation.</summary>
/// <param name="AuthorityKey">The variable of the authority.</param>
/// <param name="MetadataUrlKey">The variable of the address of the discovery document.</param>
/// <param name="ClientIdKey">The variable of the client ID.</param>
/// <param name="ClientSecretKey">The variable of the client secret.</param>
/// <param name="DisplayNameKey">The variable of the name on the sign-in page.</param>
internal sealed record InstallationSsoProviderKeys(
    string AuthorityKey,
    string MetadataUrlKey,
    string ClientIdKey,
    string ClientSecretKey,
    string DisplayNameKey);
