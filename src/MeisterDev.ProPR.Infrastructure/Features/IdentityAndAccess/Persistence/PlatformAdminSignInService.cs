// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Claims;
using System.Text.Json;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Configurations;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>
///     Signs platform administrators in through the OpenID Connect identity provider configured for the installation,
///     with the authorization code flow, PKCE, a nonce, and a state bound to the browser by a cookie.
/// </summary>
/// <remarks>
///     <para>
///         An account is found through its link to the identity at the provider, the pair of issuer and subject.
///         The link carries no tenant and no tenant provider. The sign-in never links an existing account: the
///         first sign-in with the required role creates a new account. That account carries no email address,
///         because a tenant identity provider matches existing accounts by email, and the email claim of a tenant
///         provider is controlled by the tenant's administrators. A link would let that provider sign in to an
///         account with rights over the whole installation. The email stays on the link.
///     </para>
///     <para>
///         The role in the ID token decides at every sign-in. With the role, the account holds platform
///         administrator rights. Without it, the sign-in is refused, the account loses the rights, and its sessions
///         and personal access tokens are revoked.
///     </para>
/// </remarks>
public sealed class PlatformAdminSignInService(
    MeisterProPRDbContext dbContext,
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IUserPatRepository userPatRepository,
    ISecretProtectionCodec secretProtectionCodec,
    IHttpClientFactory httpClientFactory,
    IOidcTokenValidator oidcTokenValidator,
    IUserAccountAuditLog auditLog,
    IOptions<PlatformAdminSsoOptions> options,
    ILogger<PlatformAdminSignInService> logger) : IPlatformAdminSignInService
{
    private const string FlowPurpose = "platform-admin-sso-sign-in-flow";
    private const string CallbackPath = "auth/platform-admin/callback";
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public async Task<PlatformAdminSignInChallenge?> BuildChallengeAsync(
        Uri applicationBaseUri,
        Uri frontendReturnUrl,
        CancellationToken ct = default)
    {
        var settings = options.Value;
        var metadata = await this.GetProviderMetadataAsync(settings, ct);
        if (metadata is null)
        {
            return null;
        }

        var flow = new PlatformAdminSignInFlow(
            AuthorizationCodeFlow.CreateRandomValue(),
            AuthorizationCodeFlow.CreateRandomValue(),
            AuthorizationCodeFlow.CreateRandomValue(),
            BuildCallbackUrl(applicationBaseUri),
            frontendReturnUrl.ToString(),
            DateTimeOffset.UtcNow);

        var redirectUri = AuthorizationCodeFlow.BuildAuthorizationUri(
            metadata.AuthorizationEndpoint,
            [
                new KeyValuePair<string, string>("client_id", settings.ClientId!),
                new KeyValuePair<string, string>("response_type", "code"),
                new KeyValuePair<string, string>("redirect_uri", flow.CallbackUrl),
                new KeyValuePair<string, string>("scope", "openid profile email"),
                new KeyValuePair<string, string>("state", flow.State),
                new KeyValuePair<string, string>("nonce", flow.Nonce),
                new KeyValuePair<string, string>("code_challenge", AuthorizationCodeFlow.ComputeCodeChallenge(flow.CodeVerifier)),
                new KeyValuePair<string, string>("code_challenge_method", "S256"),
            ]);

        return new PlatformAdminSignInChallenge(
            redirectUri.ToString(),
            metadata.AuthorizationEndpoint.GetLeftPart(UriPartial.Authority),
            ProtectedSignInState.Protect(secretProtectionCodec, flow, FlowPurpose));
    }

    /// <inheritdoc />
    public async Task<PlatformAdminSignInCompletion> CompleteSignInAsync(
        Uri applicationBaseUri,
        PlatformAdminSignInCallback callback,
        string? protectedFlow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var settings = options.Value;
        var flow = ProtectedSignInState.TryRead<PlatformAdminSignInFlow>(secretProtectionCodec, protectedFlow, FlowPurpose);
        if (flow is null
            || !string.Equals(flow.State, callback.State, StringComparison.Ordinal)
            || !string.Equals(flow.CallbackUrl, BuildCallbackUrl(applicationBaseUri), StringComparison.Ordinal)
            || flow.IssuedAtUtc + FlowLifetime < DateTimeOffset.UtcNow)
        {
            return this.Refuse(
                "invalid_state",
                "The sign-in request is invalid or has expired. Start the sign-in again.",
                flow?.FrontendReturnUrl);
        }

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            return this.Refuse(
                "provider_error",
                callback.ErrorDescription ?? "The identity provider refused the sign-in.",
                flow.FrontendReturnUrl,
                callback.Error);
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            return this.Refuse(
                "missing_authorization_code",
                "The identity provider did not return an authorization code.",
                flow.FrontendReturnUrl);
        }

        var metadata = await this.GetProviderMetadataAsync(settings, ct);
        if (metadata is null)
        {
            return this.Refuse(
                "provider_unavailable",
                "The identity provider for platform administrators cannot be reached. Sign in with a local account or try again later.",
                flow.FrontendReturnUrl);
        }

        var identity = await this.RedeemAndValidateAsync(settings, metadata, flow, callback.Code.Trim(), ct);
        if (identity.FailureCode is not null)
        {
            return this.Refuse(
                identity.FailureCode,
                "The identity provider did not confirm the sign-in. Start the sign-in again.",
                flow.FrontendReturnUrl);
        }

        return identity.HoldsRequiredRole
            ? await this.SignInWithRoleAsync(identity, flow.FrontendReturnUrl, ct)
            : await this.RefuseWithoutRoleAsync(identity, flow.FrontendReturnUrl, ct);
    }

    private static string BuildCallbackUrl(Uri applicationBaseUri)
    {
        return new Uri(applicationBaseUri, CallbackPath).ToString();
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        // A token endpoint can answer with valid JSON that is not an object, such as [] or null. TryGetProperty throws
        // for such a value, so the answer is treated as one without the property.
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? FirstClaim(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirst(claimType)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<OidcProviderMetadata?> GetProviderMetadataAsync(PlatformAdminSsoOptions settings, CancellationToken ct)
    {
        var metadata = await oidcTokenValidator.GetProviderMetadataAsync(settings.MetadataAddress, ct);
        if (metadata is null)
        {
            logger.LogWarning(
                "PlatformAdminSignInUnavailable MetadataAddress={MetadataAddress} Reason={Reason}",
                SecretSafeRendering.Address(settings.MetadataAddress),
                "oidc_discovery_unavailable");
            return null;
        }

        // The discovery document names the issuer every token is validated against. It must be the configured
        // authority, so a metadata address that points at another realm or provider cannot widen whose tokens count.
        if (!settings.IsAuthority(metadata.Issuer))
        {
            logger.LogWarning(
                "PlatformAdminSignInUnavailable MetadataAddress={MetadataAddress} Reason={Reason} Issuer={Issuer}",
                SecretSafeRendering.Address(settings.MetadataAddress),
                "issuer_mismatch",
                metadata.Issuer);
            return null;
        }

        return metadata;
    }

    private async Task<PlatformIdentity> RedeemAndValidateAsync(
        PlatformAdminSsoOptions settings,
        OidcProviderMetadata metadata,
        PlatformAdminSignInFlow flow,
        string authorizationCode,
        CancellationToken ct)
    {
        var (statusCode, document) = await AuthorizationCodeFlow.RedeemCodeAsync(
            httpClientFactory.CreateClient(AuthorizationCodeFlow.HttpClientName),
            metadata.TokenEndpoint,
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("client_id", settings.ClientId!),
                new KeyValuePair<string, string>("client_secret", settings.ClientSecret!),
                new KeyValuePair<string, string>("code", authorizationCode),
                new KeyValuePair<string, string>("redirect_uri", flow.CallbackUrl),
                new KeyValuePair<string, string>("code_verifier", flow.CodeVerifier),
            ],
            null,
            logger,
            ct);

        using (document)
        {
            if (document is null)
            {
                logger.LogWarning(
                    "PlatformAdminSignInRejected Reason={Reason} StatusCode={StatusCode}",
                    "token_exchange_failed",
                    (int?)statusCode);
                return PlatformIdentity.Failure("token_exchange_failed");
            }

            if (!TryGetString(document.RootElement, "id_token", out var idToken))
            {
                logger.LogWarning("PlatformAdminSignInRejected Reason={Reason}", "missing_id_token");
                return PlatformIdentity.Failure("missing_id_token");
            }

            var validation = await oidcTokenValidator.ValidateAsync(
                new OidcTokenValidationRequest(idToken, settings.MetadataAddress, settings.ClientId!, flow.Nonce),
                ct);
            if (!validation.IsValid || validation.Principal is null || !settings.IsAuthority(validation.Issuer))
            {
                var reason = validation.FailureCode ?? "unexpected_token_issuer";
                logger.LogWarning("PlatformAdminSignInRejected Reason={Reason}", reason);
                return PlatformIdentity.Failure(reason);
            }

            // The subject is an opaque identifier and is compared as the token carries it; " alice" is not "alice".
            var principal = validation.Principal;
            var subject = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                logger.LogWarning("PlatformAdminSignInRejected Reason={Reason}", "missing_identity_claims");
                return PlatformIdentity.Failure("missing_identity_claims");
            }

            var emailVerified = bool.TryParse(principal.FindFirst("email_verified")?.Value, out var verified) && verified;
            var holdsRequiredRole = principal.FindAll(settings.EffectiveRoleClaim)
                .Any(claim => string.Equals(claim.Value, settings.RequiredRole, StringComparison.Ordinal));

            return new PlatformIdentity(
                validation.Issuer!,
                subject,
                FirstClaim(principal, "email"),
                emailVerified,
                FirstClaim(principal, "preferred_username"),
                holdsRequiredRole,
                null);
        }
    }

    private async Task<PlatformAdminSignInCompletion> SignInWithRoleAsync(
        PlatformIdentity identity,
        string frontendReturnUrl,
        CancellationToken ct)
    {
        var link = await this.FindLinkAsync(identity, ct);
        if (link is not null)
        {
            var account = await dbContext.AppUsers.FirstAsync(user => user.Id == link.UserId, ct);
            if (!account.IsActive)
            {
                auditLog.PlatformIdentityProviderSignInRefused(account.Id, account.Username, identity.Issuer, identity.Subject, "account_disabled");
                return this.Refuse(
                    "account_disabled",
                    "This account is disabled. A platform administrator can enable it again under Administration → Users.",
                    frontendReturnUrl);
            }

            var restoresRights = account.GlobalRole != AppUserRole.Admin;
            account.GlobalRole = AppUserRole.Admin;
            UpdateLink(link, identity, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(ct);

            if (restoresRights)
            {
                auditLog.PlatformAdministratorRightsGranted(account.Id, account.Username, identity.Issuer, identity.Subject);
            }

            auditLog.SignedInThroughPlatformIdentityProvider(account.Id, account.Username, identity.Issuer, identity.Subject);
            return await this.SignedInAsync(account.Id, frontendReturnUrl, ct);
        }

        // External sign-in does not silently merge with a local password account. An email claim does not prove that
        // the person at the identity provider owns the account that the password protects. Every account with the
        // address is checked, because more than one account can carry it.
        var passwordAccount = identity.Email is null
            ? null
            : await this.FindPasswordAccountAsync(identity.Email.ToUpperInvariant(), ct);
        if (passwordAccount is not null)
        {
            auditLog.PlatformIdentityProviderSignInRefused(
                passwordAccount.Id,
                passwordAccount.Username,
                identity.Issuer,
                identity.Subject,
                "external_link_requires_confirmation");
            return this.Refuse(
                "external_link_requires_confirmation",
                "An account with this email address and a local password already exists. ProPR does not link it to "
                + "the identity provider. Sign in with the local password, or ask a platform administrator to resolve "
                + "the conflict.",
                frontendReturnUrl);
        }

        return await this.CreateAccountAsync(identity, frontendReturnUrl, ct);
    }

    private async Task<PlatformAdminSignInCompletion> CreateAccountAsync(
        PlatformIdentity identity,
        string frontendReturnUrl,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new AppUserRecord
        {
            Id = Guid.NewGuid(),
            Username = await ExternalAccountNames.CreateUniqueUsernameAsync(
                userRepository,
                identity.PreferredUsername ?? identity.Email ?? "administrator",
                ct),
            GlobalRole = AppUserRole.Admin,
            IsActive = true,
            CreatedAt = now,
        };
        var link = new ExternalIdentityRecord
        {
            Id = Guid.NewGuid(),
            UserId = account.Id,
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            CreatedAt = now,
        };
        UpdateLink(link, identity, now);
        dbContext.AppUsers.Add(account);
        dbContext.ExternalIdentities.Add(link);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsLinkConflict(exception))
        {
            // Two first sign-ins of one identity at the same time both try to create the account. The unique index on
            // the link refuses the second, which leaves nothing behind. Any other write failure surfaces as an error.
            logger.LogWarning(exception, "PlatformAdminSignInRejected Reason={Reason}", "sign_in_conflict");
            dbContext.ChangeTracker.Clear();
            return this.Refuse(
                "sign_in_conflict",
                "Another sign-in of this account was in progress. Start the sign-in again.",
                frontendReturnUrl);
        }

        auditLog.PlatformAdministratorRightsGranted(account.Id, account.Username, identity.Issuer, identity.Subject);
        auditLog.SignedInThroughPlatformIdentityProvider(account.Id, account.Username, identity.Issuer, identity.Subject);
        return await this.SignedInAsync(account.Id, frontendReturnUrl, ct);
    }

    private async Task<PlatformAdminSignInCompletion> RefuseWithoutRoleAsync(
        PlatformIdentity identity,
        string frontendReturnUrl,
        CancellationToken ct)
    {
        var link = await this.FindLinkAsync(identity, ct);
        AppUserRecord? account = null;
        if (link is not null)
        {
            account = await dbContext.AppUsers.FirstAsync(user => user.Id == link.UserId, ct);
            var removesRights = account.GlobalRole == AppUserRole.Admin;
            if (removesRights)
            {
                account.GlobalRole = AppUserRole.User;
                UpdateLink(link, identity, link.LastSignInAt);
                await dbContext.SaveChangesAsync(ct);
            }

            // The sessions and tokens of the account were issued while it held the rights. Every sign-in refused for the
            // missing role revokes them, whatever role is stored, so a refusal that failed after it saved the role is
            // finished by the next one. Once the role is saved, the revocations run to the end even when the request is
            // cancelled.
            await refreshTokenRepository.RevokeAllForUserAsync(account.Id, CancellationToken.None);
            await userPatRepository.RevokeAllForUserAsync(account.Id, CancellationToken.None);

            if (removesRights)
            {
                auditLog.PlatformAdministratorRightsRevoked(account.Id, account.Username, identity.Issuer, identity.Subject);

                // The identity provider decides, so the last active administrator loses the rights as well. The next
                // start creates the bootstrap administrator again, and refuses to start without its settings.
                if (await userRepository.CountActiveAdminsAsync(ct) == 0)
                {
                    logger.LogWarning(
                        "No active platform administrator remains after a sign-in without the role {RequiredRole}. Give "
                        + "an account the role at the identity provider, or restart ProPR with MEISTER_BOOTSTRAP_ADMIN_USER "
                        + "and MEISTER_BOOTSTRAP_ADMIN_PASSWORD set to create a local one.",
                        options.Value.RequiredRole);
                }
            }
        }

        auditLog.PlatformIdentityProviderSignInRefused(
            account?.Id,
            account?.Username,
            identity.Issuer,
            identity.Subject,
            "platform_role_missing");
        return this.Refuse(
            "platform_role_missing",
            "Your account at the identity provider does not hold the role for platform administrators of this installation.",
            frontendReturnUrl);
    }

    /// <summary>Whether a write failed because another sign-in created the link of the same identity first.</summary>
    internal static bool IsLinkConflict(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ExternalIdentityEntityTypeConfiguration.PlatformSubjectIndexName,
        };
    }

    private Task<AppUserRecord?> FindPasswordAccountAsync(string normalizedEmail, CancellationToken ct)
    {
        return dbContext.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                user => user.NormalizedEmail == normalizedEmail && user.PasswordHash != null && user.PasswordHash != string.Empty,
                ct);
    }

    private Task<ExternalIdentityRecord?> FindLinkAsync(PlatformIdentity identity, CancellationToken ct)
    {
        return dbContext.ExternalIdentities.FirstOrDefaultAsync(
            link => link.TenantId == null && link.Issuer == identity.Issuer && link.Subject == identity.Subject,
            ct);
    }

    private static void UpdateLink(ExternalIdentityRecord link, PlatformIdentity identity, DateTimeOffset? lastSignInAt)
    {
        link.Email = identity.Email ?? string.Empty;
        link.EmailVerified = identity.EmailVerified;
        link.LastSignInAt = lastSignInAt;
    }

    private async Task<PlatformAdminSignInCompletion> SignedInAsync(Guid userId, string frontendReturnUrl, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct)
                   ?? throw new InvalidOperationException($"The account {userId} was not found after the sign-in saved it.");
        return new PlatformAdminSignInCompletion(user, frontendReturnUrl);
    }

    private PlatformAdminSignInCompletion Refuse(
        string failureCode,
        string failureMessage,
        string? frontendReturnUrl,
        string? providerError = null)
    {
        logger.LogWarning(
            "PlatformAdminSignInRejected Reason={Reason} ProviderError={ProviderError}",
            failureCode,
            providerError?.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
        return new PlatformAdminSignInCompletion(null, frontendReturnUrl, failureCode, failureMessage);
    }

    /// <summary>The state of one sign-in, which the browser keeps in a protected cookie until the callback.</summary>
    private sealed record PlatformAdminSignInFlow(
        string State,
        string Nonce,
        string CodeVerifier,
        string CallbackUrl,
        string FrontendReturnUrl,
        DateTimeOffset IssuedAtUtc)
    {
        /// <summary>Renders the flow without its nonce and code verifier; see <see cref="SecretSafeRendering" />.</summary>
        public override string ToString()
        {
            return $"{nameof(PlatformAdminSignInFlow)} {{ CallbackUrl = {this.CallbackUrl}, "
                   + $"CodeVerifier = {SecretSafeRendering.Elide(this.CodeVerifier)}, IssuedAtUtc = {this.IssuedAtUtc:O} }}";
        }
    }

    /// <summary>The identity at the provider, as the validated ID token states it.</summary>
    private sealed record PlatformIdentity(
        string Issuer,
        string Subject,
        string? Email,
        bool EmailVerified,
        string? PreferredUsername,
        bool HoldsRequiredRole,
        string? FailureCode)
    {
        public static PlatformIdentity Failure(string failureCode)
        {
            return new PlatformIdentity(string.Empty, string.Empty, null, false, null, false, failureCode);
        }
    }
}
