// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     A tenant sign-in whose identity waits in the browser for the confirmation of its link to an account, or for a
///     new account.
/// </summary>
/// <param name="TenantSlug">The slug of the tenant whose provider confirmed the identity.</param>
/// <param name="TenantDisplayName">The name of that tenant.</param>
/// <param name="ProviderDisplayName">The name of the provider within the tenant.</param>
/// <param name="ProviderLabel">The kind of the provider, such as Microsoft, Google or GitHub.</param>
/// <param name="Email">The email address that the provider stated for the identity.</param>
/// <param name="CanCreateAccount">Whether ProPR can create a new account for the identity.</param>
/// <param name="ExpiresAt">When the pending link expires.</param>
public sealed record TenantPendingLinkDto(
    string TenantSlug,
    string TenantDisplayName,
    string ProviderDisplayName,
    string ProviderLabel,
    string Email,
    bool CanCreateAccount,
    DateTimeOffset ExpiresAt);
