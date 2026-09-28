// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Models;

/// <summary>Credential verifier and metadata used during authentication and persistence.</summary>
public sealed record TenantMachineCredential(
    Guid Id,
    Guid TenantId,
    string Label,
    string TokenHash,
    string TokenLookupHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    Guid IssuedByUserId)
{
    public override string ToString() =>
        $"TenantMachineCredential {{ Id = {Id}, TenantId = {TenantId}, TokenHash = [REDACTED], TokenLookupHash = [REDACTED] }}";
}

/// <summary>The authenticated machine identity, without its stored verifier.</summary>
public sealed record TenantMachineIdentity(Guid Id, Guid TenantId);

/// <summary>Credential metadata returned without the issued token or verifier.</summary>
public sealed record TenantMachineCredentialSummary(
    Guid Id,
    Guid TenantId,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary>A newly issued credential whose token is returned once.</summary>
public sealed record IssuedTenantMachineCredential(Guid Id, Guid TenantId, string Label, string Token, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt)
{
    public override string ToString() => $"IssuedTenantMachineCredential {{ Id = {Id}, TenantId = {TenantId}, Token = [REDACTED] }}";
}
