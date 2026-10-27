// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The sign-in of platform administrators through the OpenID Connect identity provider configured for the
///     installation. At every sign-in, the role in the ID token decides whether the account holds platform
///     administrator rights.
/// </summary>
public interface IPlatformAdminSignInService : IInstallationSignInService
{
}
