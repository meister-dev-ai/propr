// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     ADO service principal credentials attached to a client.
///     Crosses the Application → Infrastructure boundary so Infrastructure can build
///     a <c>ClientSecretCredential</c> without leaking Azure.Core types into Application.
/// </summary>
/// <param name="TenantId">Azure AD tenant GUID or domain.</param>
/// <param name="ClientId">Azure AD application (client) GUID.</param>
/// <param name="Secret">Client secret value — never serialised to JSON responses.</param>
public sealed record ClientAdoCredentials(string TenantId, string ClientId, string Secret);
