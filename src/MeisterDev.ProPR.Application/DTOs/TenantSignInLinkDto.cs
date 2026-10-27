// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>A sign-in through a provider of a tenant that is linked to an account.</summary>
/// <param name="TenantSlug">The slug of the tenant of the provider.</param>
/// <param name="TenantDisplayName">The name of that tenant.</param>
/// <param name="ProviderDisplayName">The name of the provider within the tenant.</param>
/// <param name="ProviderLabel">The kind of the provider, such as Microsoft, Google or GitHub.</param>
/// <param name="Email">The email address that the provider stated at the last sign-in.</param>
/// <param name="LinkedAt">When the link was made.</param>
/// <param name="LastSignInAt">When the account last signed in through the provider, or null.</param>
public sealed record TenantSignInLinkDto(
    string TenantSlug,
    string TenantDisplayName,
    string ProviderDisplayName,
    string ProviderLabel,
    string Email,
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastSignInAt);
