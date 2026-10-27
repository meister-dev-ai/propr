// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The sign-in of provisioned users through the OpenID Connect identity provider that the installation configures for
///     them. The sign-in reaches only an account that a hosting platform provisioned for the issuer of the provider and
///     the subject of the person, and it creates no account.
/// </summary>
public interface IProvisionedUserSignInService : IInstallationSignInService
{
}
