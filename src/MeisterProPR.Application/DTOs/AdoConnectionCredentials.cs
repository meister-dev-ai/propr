// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Azure DevOps runtime credentials resolved from a client SCM connection.
///     Crosses the Application -> Infrastructure boundary so Infrastructure can build the
///     appropriate VSS credential type without leaking Azure or VSS SDK types into Application.
/// </summary>
public sealed record AdoConnectionCredentials(
    ScmAuthenticationKind AuthenticationKind,
    string Secret,
    string? OAuthTenantId = null,
    string? OAuthClientId = null,
    string? UserName = null)
{
    public static AdoConnectionCredentials ForOAuthClientCredentials(string tenantId, string clientId, string secret)
    {
        return new AdoConnectionCredentials(
            ScmAuthenticationKind.OAuthClientCredentials,
            secret,
            tenantId,
            clientId);
    }

    public static AdoConnectionCredentials ForPersonalAccessToken(string secret)
    {
        return new AdoConnectionCredentials(ScmAuthenticationKind.PersonalAccessToken, secret);
    }

    public static AdoConnectionCredentials ForWindowsUserAccount(string userName, string secret)
    {
        return new AdoConnectionCredentials(
            ScmAuthenticationKind.WindowsUserAccount,
            secret,
            UserName: userName);
    }
}
