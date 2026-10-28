// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     The code and the messages with which ProPR refuses the password sign-in, and every request that would set a
///     password, while the installation turns the password sign-in off with <c>MEISTER_LOCAL_SIGN_IN_ENABLED</c>.
/// </summary>
internal static class LocalSignInRefusal
{
    /// <summary>The code of the refusal, which a caller can act on without reading the message.</summary>
    public const string Code = "local_sign_in_disabled";

    /// <summary>The message of a refused sign-in with a user name and a password.</summary>
    public const string SignInMessage =
        "This installation does not offer the password sign-in. Sign in through an identity provider of the installation.";

    /// <summary>The message of a refused password change.</summary>
    public const string PasswordChangeMessage =
        "This installation does not offer the password sign-in, so ProPR does not change passwords.";

    /// <summary>The message of a refused account with a password.</summary>
    public const string AccountCreationMessage =
        "This installation does not offer the password sign-in, so ProPR does not create accounts with a password.";
}
