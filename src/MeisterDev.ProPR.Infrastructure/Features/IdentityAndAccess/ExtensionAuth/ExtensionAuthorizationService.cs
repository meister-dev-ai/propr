// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.ExtensionAuth;

/// <summary>Issues expiring PKCE-bound codes using an active ProPR website session.</summary>
public sealed class ExtensionAuthorizationService(
    MeisterProPRDbContext db,
    ISecretProtectionCodec codec,
    ExternalSignInRedemptions redemptions,
    IUserRepository users,
    IRefreshTokenRepository sessions,
    SessionPolicy sessionPolicy,
    IJwtTokenService jwt,
    ExtensionAuthorizationOptions options,
    TimeProvider? clock = null) : IExtensionAuthorizationService
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private const string RequestPurpose = "propr-extension-authorization-request-v1";
    private const string CodePurpose = "propr-extension-authorization-code-v1";

    private static readonly string[] Permissions =
        ["Read review findings, status, history and cost", "Request reviews and act on findings within your existing permissions"];

    public ExtensionSignInOptionsDto GetOptions() => new(
        1, options.Enabled,
        options.Enabled ? null : options.UnavailableReason ?? "not_configured", options.Issuer,
        options.Enabled ? options.Issuer + "/auth/extension/authorize" : null,
        options.Enabled ? options.Issuer + "/auth/extension/token" : null, ["S256"]);

    public async Task<ExtensionAuthorizationStart> StartAsync(ExtensionAuthorizationRequest request, string? refreshCookie, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return new(Error: "sign_in_unavailable");
        }

        if (!this.ValidClient(request.ClientId, request.RedirectUri))
        {
            return new(Error: "invalid_request");
        }

        if (request.ResponseType != "code" || request.CodeChallengeMethod != "S256"
                                           || !Regex.IsMatch(request.CodeChallenge ?? "", "^[A-Za-z0-9_-]{43}$")
                                           || string.IsNullOrWhiteSpace(request.State) || request.State.Length > 1024
                                           || request.Prompt is not (null or "none"))
        {
            return this.Redirect(request, error: "invalid_request");
        }

        var session = await this.WebSessionAsync(refreshCookie, ct);
        if (session is not null)
        {
            var grant = await this.ActiveGrantAsync(session.UserId, ct);
            if (grant is not null)
            {
                return this.Code(request, session, grant.Id);
            }
        }

        if (request.Prompt == "none")
        {
            return this.Redirect(request, error: session is null ? "login_required" : "consent_required");
        }

        var pending = ProtectedSignInState.Protect(
            codec,
            new Pending(request, options.Issuer!, this.Now.AddMinutes(10), AuthorizationCodeFlow.CreateRandomValue()), RequestPurpose);
        return new(PendingRequest: pending);
    }

    public async Task<ExtensionConsentDto?> GetConsentAsync(string request, Guid userId, string? refreshCookie, CancellationToken ct)
    {
        var pending = this.ReadPending(request);
        var session = await this.WebSessionAsync(refreshCookie, ct);
        if (pending is null || session?.UserId != userId || await redemptions.IsRedeemedAsync("extension-request:" + pending.Nonce, ct))
        {
            return null;
        }

        return new("ProPR Lens", options.Issuer!, userId.ToString(), Permissions, pending.ExpiresAt);
    }

    public async Task<ExtensionAuthorizationStart> DecideAsync(string request, Guid userId, string? refreshCookie, bool approve, CancellationToken ct)
    {
        var pending = this.ReadPending(request);
        var session = await this.WebSessionAsync(refreshCookie, ct);
        if (pending is null || session?.UserId != userId)
        {
            return new(Error: "invalid_request");
        }

        if (!await redemptions.TryRedeemAsync("extension-request:" + pending.Nonce, pending.ExpiresAt, ct))
        {
            return new(Error: "invalid_request");
        }

        if (!approve)
        {
            return this.Redirect(pending.Request, error: "access_denied");
        }

        var grant = await this.ActiveGrantAsync(userId, ct);
        if (grant is null)
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            var superseded = db.ExtensionAuthorizationGrants.Where(value => value.UserId == userId
                                                                            && value.ClientId == ExtensionAuthorizationOptions.ClientId
                                                                            && value.ScopeVersion != ExtensionAuthorizationGrant.CurrentScopeVersion
                                                                            && value.RevokedAt == null);
            var revokedAt = this.Now;
            if (db.Database.IsRelational())
            {
                await superseded.ExecuteUpdateAsync(update => update.SetProperty(value => value.RevokedAt, revokedAt), ct);
            }
            else
            {
                foreach (var previous in await superseded.ToListAsync(ct))
                {
                    previous.RevokedAt = revokedAt;
                }

                await db.SaveChangesAsync(ct);
            }

            var consent = new ExtensionAuthorizationGrant { UserId = userId, ClientId = ExtensionAuthorizationOptions.ClientId, GrantedAt = this.Now };
            grant = new()
            {
                Id = consent.Id, UserId = consent.UserId, ClientId = consent.ClientId, ScopeVersion = consent.ScopeVersion, GrantedAt = consent.GrantedAt
            };
            db.ExtensionAuthorizationGrants.Add(grant);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                                                      {
                                                          SqlState: PostgresErrorCodes.UniqueViolation,
                                                          ConstraintName: "ix_extension_grants_active_user_client"
                                                      })
            {
                db.Entry(grant).State = EntityState.Detached;
                grant = await this.ActiveGrantAsync(userId, ct);
                if (grant is null)
                {
                    return new(Error: "invalid_request");
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }

        return this.Code(pending.Request, session, grant.Id);
    }

    public async Task<ExtensionTokenDto?> ExchangeAsync(ExtensionTokenRequest request, CancellationToken ct)
    {
        if (!options.Enabled || request.GrantType != "authorization_code" || !this.ValidClient(request.ClientId, request.RedirectUri)
            || !Regex.IsMatch(request.CodeVerifier ?? "", "^[A-Za-z0-9._~-]{43,128}$") || request.Code?.Length > 16384)
        {
            return null;
        }

        var code = ProtectedSignInState.TryRead<CodePayload>(codec, request.Code, CodePurpose);
        if (code is null || code.ExpiresAt <= this.Now || code.Issuer != options.Issuer
            || code.ClientId != request.ClientId || code.RedirectUri != request.RedirectUri)
        {
            return null;
        }

        var challenge = AuthorizationCodeFlow.ComputeCodeChallenge(request.CodeVerifier!);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(code.Challenge)))
        {
            return null;
        }

        if (!await this.IsAccessActiveAsync(code.UserId, code.GrantId, code.SessionId, ct))
        {
            return null;
        }

        if (!await redemptions.TryRedeemAsync("extension-code:" + code.Nonce, code.ExpiresAt, ct))
        {
            return null;
        }

        var user = await users.GetByIdAsync(code.UserId, ct);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        await sessions.TouchLastUsedAsync(code.SessionId, this.Now, ct);
        return new(jwt.GenerateExtensionAccessToken(user, code.GrantId, code.SessionId), "Bearer", 900, user.Id.ToString(), options.Issuer!);
    }

    public async Task<bool> IsAccessActiveAsync(Guid userId, Guid grantId, Guid sessionId, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return false;
        }

        // ProPR calls this for every request with an extension token, so one query checks the grant, the website session
        // and the account.
        var activeSessions = RefreshTokenRepository.Active(db.RefreshTokens, sessionPolicy, this.Now);
        return await db.ExtensionAuthorizationGrants.AsNoTracking().AnyAsync(
            grant => grant.Id == grantId && grant.UserId == userId && grant.ClientId == ExtensionAuthorizationOptions.ClientId
                     && grant.ScopeVersion == ExtensionAuthorizationGrant.CurrentScopeVersion && grant.RevokedAt == null
                     && activeSessions.Any(session => session.Id == sessionId && session.UserId == userId)
                     && db.AppUsers.Any(user => user.Id == userId && user.IsActive),
            ct);
    }

    public async Task<IReadOnlyList<ExtensionGrantDto>> ListGrantsAsync(Guid userId, CancellationToken ct) =>
        await db.ExtensionAuthorizationGrants.AsNoTracking().Where(value => value.UserId == userId && value.RevokedAt == null)
            .Select(value => new ExtensionGrantDto(value.Id, "ProPR Lens", value.GrantedAt)).ToListAsync(ct);

    public async Task RevokeAsync(Guid userId, Guid grantId, CancellationToken ct)
    {
        // A grant that is revoked already keeps the time of its first revocation.
        var value = await db.ExtensionAuthorizationGrants.SingleOrDefaultAsync(
            grant => grant.Id == grantId && grant.UserId == userId && grant.RevokedAt == null, ct);
        if (value is null)
        {
            return;
        }

        value.RevokedAt = this.Now;
        await db.SaveChangesAsync(ct);
    }

    private bool ValidClient(string? clientId, string? redirect) => clientId == ExtensionAuthorizationOptions.ClientId
                                                                    && redirect is not null && options.RedirectUris.Contains(redirect, StringComparer.Ordinal);

    private Pending? ReadPending(string? request)
    {
        if (!options.Enabled || request?.Length > 16384)
        {
            return null;
        }

        var value = ProtectedSignInState.TryRead<Pending>(codec, request, RequestPurpose);
        return value is not null && value.ExpiresAt > this.Now && value.Issuer == options.Issuer
               && this.ValidClient(value.Request.ClientId, value.Request.RedirectUri)
            ? value
            : null;
    }

    private async Task<RefreshToken?> WebSessionAsync(string? raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var session = await sessions.GetActiveByHashAsync(hash, ct);
        if (session is null)
        {
            return null;
        }

        return await users.GetByIdAsync(session.UserId, ct) is { IsActive: true } ? session : null;
    }

    private Task<ExtensionAuthorizationGrantRecord?> ActiveGrantAsync(Guid userId, CancellationToken ct) =>
        db.ExtensionAuthorizationGrants.AsNoTracking().SingleOrDefaultAsync(
            value => value.UserId == userId
                     && value.ClientId == ExtensionAuthorizationOptions.ClientId && value.ScopeVersion == ExtensionAuthorizationGrant.CurrentScopeVersion &&
                     value.RevokedAt == null, ct);

    private ExtensionAuthorizationStart Code(ExtensionAuthorizationRequest request, RefreshToken session, Guid grantId)
    {
        var code = ProtectedSignInState.Protect(
            codec, new CodePayload(
                request.ClientId, request.RedirectUri,
                request.CodeChallenge, options.Issuer!, session.UserId, session.Id, grantId,
                this.Now.AddSeconds(60), AuthorizationCodeFlow.CreateRandomValue()), CodePurpose);
        return this.Redirect(request, code);
    }

    private ExtensionAuthorizationStart Redirect(ExtensionAuthorizationRequest request, string? code = null, string? error = null)
    {
        var parameters = new Dictionary<string, string> { ["state"] = request.State, ["iss"] = options.Issuer! };
        if (code is not null)
        {
            parameters["code"] = code;
        }

        if (error is not null)
        {
            parameters["error"] = error;
        }

        return new(RedirectUrl: AuthorizationCodeFlow.BuildAuthorizationUri(new Uri(request.RedirectUri), parameters).AbsoluteUri);
    }

    private sealed record Pending(ExtensionAuthorizationRequest Request, string Issuer, DateTimeOffset ExpiresAt, string Nonce);

    private sealed record CodePayload(
        string ClientId,
        string RedirectUri,
        string Challenge,
        string Issuer,
        Guid UserId,
        Guid SessionId,
        Guid GrantId,
        DateTimeOffset ExpiresAt,
        string Nonce);
}
