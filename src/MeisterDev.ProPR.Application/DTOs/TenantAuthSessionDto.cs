// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Tenant-authenticated session payload returned by tenant login endpoints.</summary>
public sealed record TenantAuthSessionDto(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn = 900,
    string TokenType = "Bearer")
{
    /// <summary>Renders the session without either token; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(TenantAuthSessionDto)} {{ AccessToken = {SecretSafeRendering.Elide(this.AccessToken)}, "
               + $"RefreshToken = {SecretSafeRendering.Elide(this.RefreshToken)}, "
               + $"ExpiresIn = {this.ExpiresIn}, TokenType = {this.TokenType} }}";
    }
}
