// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Tenant-authenticated session payload returned by tenant login endpoints.</summary>
public sealed record TenantAuthSessionDto(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn = 900,
    string TokenType = "Bearer");
