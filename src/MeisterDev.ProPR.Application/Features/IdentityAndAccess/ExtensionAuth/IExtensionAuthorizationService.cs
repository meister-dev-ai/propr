// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;

/// <summary>Authorizes a public browser extension through the existing website session.</summary>
public interface IExtensionAuthorizationService
{
    ExtensionSignInOptionsDto GetOptions();
    Task<ExtensionAuthorizationStart> StartAsync(ExtensionAuthorizationRequest request, string? refreshCookie, CancellationToken ct);
    Task<ExtensionConsentDto?> GetConsentAsync(string request, Guid userId, string? refreshCookie, CancellationToken ct);
    Task<ExtensionAuthorizationStart> DecideAsync(string request, Guid userId, string? refreshCookie, bool approve, CancellationToken ct);
    Task<ExtensionTokenDto?> ExchangeAsync(ExtensionTokenRequest request, CancellationToken ct);
    Task<bool> IsAccessActiveAsync(Guid userId, Guid grantId, Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<ExtensionGrantDto>> ListGrantsAsync(Guid userId, CancellationToken ct);
    Task RevokeAsync(Guid userId, Guid grantId, CancellationToken ct);
}

public sealed record ExtensionSignInOptionsDto(
    int ProtocolVersion,
    bool Enabled,
    string? Reason,
    string? Issuer,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    IReadOnlyList<string> SupportedPkceMethods);

public sealed record ExtensionAuthorizationRequest(
    string ClientId,
    string RedirectUri,
    string ResponseType,
    string CodeChallenge,
    string CodeChallengeMethod,
    string State,
    string? Prompt = null);

public sealed record ExtensionAuthorizationStart(string? RedirectUrl = null, string? PendingRequest = null, string? Error = null)
{
    public override string ToString() => "ExtensionAuthorizationStart { redacted }";
}

public sealed record ExtensionConsentDto(string Application, string Issuer, string Subject, IReadOnlyList<string> Permissions, DateTimeOffset ExpiresAt);

public sealed record ExtensionDecisionRequest(string Request, bool Approve)
{
    public override string ToString() => "ExtensionDecisionRequest { redacted }";
}

public sealed record ExtensionTokenRequest(string ClientId, string RedirectUri, string Code, string CodeVerifier, string GrantType)
{
    public override string ToString() => "ExtensionTokenRequest { redacted }";
}

public sealed record ExtensionTokenDto(string AccessToken, string TokenType, int ExpiresIn, string Subject, string Issuer)
{
    public override string ToString() => "ExtensionTokenDto { AccessToken = [REDACTED] }";
}

public sealed record ExtensionGrantDto(Guid Id, string Application, DateTimeOffset GrantedAt);
