// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     The code and the messages with which ProPR refuses the sign-in through the identity providers of tenants, and every
///     request that would create or enable such a provider, while the installation turns these providers off with
///     <c>MEISTER_TENANT_SSO_ENABLED</c>.
/// </summary>
internal static class TenantSsoRefusal
{
    /// <summary>The code of the refusal, which a caller can act on without reading the message.</summary>
    public const string Code = "tenant_sso_disabled";

    /// <summary>The message of a refused sign-in through an identity provider of a tenant.</summary>
    public const string SignInMessage =
        "This installation does not offer the sign-in through the identity providers of tenants. Sign in through an "
        + "identity provider of the installation.";

    /// <summary>The message of a refused provider that would be enabled.</summary>
    public const string ProviderMessage =
        "This installation does not allow identity providers of tenants, so ProPR does not create or enable one.";
}
