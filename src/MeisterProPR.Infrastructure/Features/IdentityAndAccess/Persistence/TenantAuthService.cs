// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>Tenant-aware authentication service for local and external sign-in flows.</summary>
public sealed class TenantAuthService(
    MeisterProPRDbContext dbContext,
    IUserRepository userRepository,
    IPasswordHashService passwordHashService,
    ISecretProtectionCodec secretProtectionCodec,
    IHttpClientFactory httpClientFactory,
    ILogger<TenantAuthService> logger) : ITenantAuthService
{
    private const string ClientSecretPurpose = "tenant-sso-provider-client-secret";
    private const string ChallengeStatePurpose = "tenant-sso-external-auth-state";
    private static readonly TimeSpan ChallengeStateLifetime = TimeSpan.FromMinutes(15);

    public async Task<TenantLoginOptionsDto?> GetLoginOptionsAsync(string tenantSlug, CancellationToken ct = default)
    {
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.Slug == tenantSlug, ct);

        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning(
                "TenantLoginOptionsUnavailable TenantSlug={TenantSlug} Reason={Reason}",
                tenantSlug,
                tenant is null ? "tenant_not_found" : "tenant_inactive");
            return null;
        }

        var providers = await dbContext.TenantSsoProviders
            .AsNoTracking()
            .Where(provider => provider.TenantId == tenant.Id && provider.IsEnabled)
            .OrderBy(provider => provider.DisplayName)
            .ToListAsync(ct);

        var providerOptions = providers
            .Select(provider => new TenantLoginProviderDto(
                provider.Id,
                provider.DisplayName,
                provider.ProviderKind,
                GetProviderLabel(provider.ProviderKind)))
            .ToList();

        logger.LogInformation(
            "TenantLoginOptionsResolved TenantSlug={TenantSlug} LocalLoginEnabled={LocalLoginEnabled} ProviderCount={ProviderCount}",
            tenant.Slug,
            tenant.LocalLoginEnabled,
            providerOptions.Count);

        return new TenantLoginOptionsDto(tenant.Slug, tenant.LocalLoginEnabled, providerOptions);
    }

    public async Task<AppUser?> AuthenticateLocalAsync(string tenantSlug, string username, string password, CancellationToken ct = default)
    {
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.Slug == tenantSlug, ct);

        if (tenant is null || !tenant.IsActive || !tenant.LocalLoginEnabled)
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                tenantSlug,
                username,
                tenant is null ? "tenant_not_found" : !tenant.IsActive ? "tenant_inactive" : "local_login_disabled");
            return null;
        }

        var user = await userRepository.GetByUsernameAsync(username, ct);
        if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                tenantSlug,
                username,
                user is null ? "user_not_found" : !user.IsActive ? "user_inactive" : "missing_password_hash");
            return null;
        }

        if (!passwordHashService.Verify(password, user.PasswordHash))
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                tenantSlug,
                username,
                "invalid_password");
            return null;
        }

        var membership = await userRepository.GetTenantMembershipAsync(tenant.Id, user.Id, ct);
        if (membership is null)
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                tenantSlug,
                username,
                "missing_tenant_membership");
            return null;
        }

        logger.LogInformation(
            "TenantLocalLoginSucceeded TenantSlug={TenantSlug} UserId={UserId}",
            tenantSlug,
            user.Id);
        return user;
    }

    public async Task<TenantExternalChallengeResult?> BuildExternalChallengeAsync(
        string tenantSlug,
        Guid providerId,
        Uri applicationBaseUri,
        Uri? frontendReturnUrl = null,
        CancellationToken ct = default)
    {
        var provider = await this.GetActiveProviderAsync(tenantSlug, providerId, true, ct);
        if (provider is null)
        {
            logger.LogWarning(
                "TenantExternalChallengeUnavailable TenantSlug={TenantSlug} ProviderId={ProviderId}",
                tenantSlug,
                providerId);
            return null;
        }

        var providerConfiguration = TryResolveProviderConfiguration(provider);
        if (providerConfiguration is null)
        {
            logger.LogWarning(
                "TenantExternalChallengeUnavailable TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason}",
                tenantSlug,
                providerId,
                "unsupported_provider_configuration");
            return null;
        }

        var callbackUrl = BuildCallbackUrl(applicationBaseUri, tenantSlug);
        var protectedState = this.ProtectChallengeState(tenantSlug, providerId, callbackUrl, frontendReturnUrl?.ToString());
        var redirectUrl = BuildAuthorizationUrl(provider, providerConfiguration, callbackUrl, protectedState);

        logger.LogInformation(
            "TenantExternalChallengeBuilt TenantSlug={TenantSlug} ProviderId={ProviderId} CallbackUrl={CallbackUrl}",
            tenantSlug,
            providerId,
            callbackUrl);

        return new TenantExternalChallengeResult(redirectUrl, callbackUrl, protectedState);
    }

    public async Task<TenantExternalSignInCompletionResult> CompleteExternalSignInAsync(
        string tenantSlug,
        Uri applicationBaseUri,
        TenantExternalCallbackRequest callback,
        CancellationToken ct = default)
    {
        var callbackUrl = BuildCallbackUrl(applicationBaseUri, tenantSlug);
        var challengeState = this.TryValidateChallengeState(callback.State, tenantSlug, callbackUrl);
        if (challengeState is null)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} Reason={Reason}",
                tenantSlug,
                "invalid_state");
            return Failure("invalid_state", "The tenant sign-in request is invalid or has expired.");
        }

        var frontendReturnUrl = challengeState.FrontendReturnUrl;
        var providerId = challengeState.ProviderId;

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason}",
                tenantSlug,
                providerId,
                $"provider_error:{callback.Error}");
            return Failure(
                "provider_error",
                callback.ErrorDescription ?? "The external identity provider rejected the sign-in request.",
                frontendReturnUrl);
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason}",
                tenantSlug,
                providerId,
                "missing_authorization_code");
            return Failure(
                "missing_authorization_code",
                "The external identity provider did not return an authorization code.",
                frontendReturnUrl);
        }

        var provider = await this.GetActiveProviderAsync(tenantSlug, providerId, false, ct);
        if (provider is null)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason}",
                tenantSlug,
                providerId,
                "tenant_or_provider_inactive");
            return Failure(
                "tenant_or_provider_inactive",
                "The tenant sign-in provider is unavailable.",
                frontendReturnUrl);
        }

        var providerConfiguration = TryResolveProviderConfiguration(provider);
        if (providerConfiguration is null)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason}",
                tenantSlug,
                providerId,
                "unsupported_provider_configuration");
            return Failure(
                "unsupported_provider_configuration",
                "The tenant sign-in provider configuration is unsupported.",
                frontendReturnUrl);
        }

        var payload = await this.ResolveExternalIdentityPayloadAsync(
            provider,
            providerConfiguration,
            callback.Code.Trim(),
            callbackUrl,
            ct);

        if (payload is null || string.IsNullOrWhiteSpace(payload.Issuer) || string.IsNullOrWhiteSpace(payload.Subject))
        {
            return Failure(
                "external_identity_rejected",
                "External identity rejected by tenant policy.",
                frontendReturnUrl);
        }

        try
        {
            var user = await this.CompleteExternalSignInAsync(provider, payload, ct);
            return user is null
                ? Failure("external_identity_rejected", "External identity rejected by tenant policy.", frontendReturnUrl)
                : new TenantExternalSignInCompletionResult(user, frontendReturnUrl);
        }
        catch (TenantExternalSignInPolicyException exception)
        {
            return Failure(exception.FailureCode, exception.Message, frontendReturnUrl);
        }
    }

    private async Task<AppUser?> CompleteExternalSignInAsync(
        TenantSsoProviderRecord provider,
        TenantExternalIdentityPayload payload,
        CancellationToken ct)
    {
        var linkedUser = await userRepository.GetByExternalIdentityAsync(
            provider.TenantId,
            provider.Id,
            payload.Issuer,
            payload.Subject,
            ct);

        if (linkedUser is not null)
        {
            if (!linkedUser.IsActive)
            {
                return null;
            }

            var membership = await userRepository.GetTenantMembershipAsync(provider.TenantId, linkedUser.Id, ct);
            if (membership is null)
            {
                return null;
            }

            await this.TouchExternalIdentityAsync(provider.TenantId, provider.Id, payload, ct);
            return linkedUser;
        }

        if (!payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Email))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "email_not_verified");

            throw new TenantExternalSignInPolicyException(
                "email_not_verified",
                "The identity provider did not return a verified email address for this sign-in.");
        }

        var email = payload.Email.Trim();
        var normalizedEmail = email.ToUpperInvariant();
        var domain = GetEmailDomain(email);
        if (domain is null)
        {
            return null;
        }

        if (provider.AllowedEmailDomains.Length > 0
            && !provider.AllowedEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} EmailDomain={EmailDomain}",
                provider.TenantId,
                provider.Id,
                "disallowed_domain",
                domain);

            throw new TenantExternalSignInPolicyException(
                "disallowed_domain",
                $"Email domain '{domain}' is not allowed for this tenant sign-in provider.");
        }

        var existingUser = await userRepository.GetByNormalizedEmailAsync(normalizedEmail, ct);
        if (existingUser is not null)
        {
            var membership = await userRepository.GetTenantMembershipAsync(provider.TenantId, existingUser.Id, ct);
            if (membership is not null)
            {
                if (!existingUser.IsActive)
                {
                    return null;
                }

                await this.LinkExternalIdentityAsync(provider.TenantId, provider.Id, existingUser.Id, payload, ct);
                logger.LogInformation(
                    "TenantExternalIdentityLinked TenantId={TenantId} ProviderId={ProviderId} UserId={UserId}",
                    provider.TenantId,
                    provider.Id,
                    existingUser.Id);
                return existingUser;
            }

            if (!provider.AutoCreateUsers)
            {
                logger.LogWarning(
                    "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} UserId={UserId}",
                    provider.TenantId,
                    provider.Id,
                    "auto_create_disabled_existing_user_without_membership",
                    existingUser.Id);

                throw new TenantExternalSignInPolicyException(
                    "auto_create_disabled",
                    "First-time sign-in is disabled for this provider. Ask a tenant administrator to enable sign-in for your account.");
            }

            if (!existingUser.IsActive)
            {
                return null;
            }

            await this.EnsureTenantMembershipAsync(provider.TenantId, existingUser.Id, ct);
            await this.LinkExternalIdentityAsync(provider.TenantId, provider.Id, existingUser.Id, payload, ct);
            logger.LogInformation(
                "TenantExternalIdentityLinkedWithMembershipProvisioned TenantId={TenantId} ProviderId={ProviderId} UserId={UserId}",
                provider.TenantId,
                provider.Id,
                existingUser.Id);
            return existingUser;
        }

        if (!provider.AutoCreateUsers)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "auto_create_disabled");

            throw new TenantExternalSignInPolicyException(
                "auto_create_disabled",
                "First-time sign-in is disabled for this provider. Ask a tenant administrator to enable sign-in for your account.");
        }

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = await this.GenerateUniqueUsernameAsync(email, ct),
            Email = email,
            NormalizedEmail = normalizedEmail,
            GlobalRole = AppUserRole.User,
            IsActive = true,
            CreatedAt = now,
        };

        await userRepository.AddAsync(user, ct);
        await this.EnsureTenantMembershipAsync(provider.TenantId, user.Id, ct, now);

        await this.LinkExternalIdentityAsync(provider.TenantId, provider.Id, user.Id, payload, ct);

        logger.LogInformation(
            "TenantExternalUserProvisioned TenantId={TenantId} ProviderId={ProviderId} UserId={UserId} Username={Username}",
            provider.TenantId,
            provider.Id,
            user.Id,
            user.Username);

        return user;
    }

    private async Task EnsureTenantMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct, DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        await userRepository.UpsertTenantMembershipAsync(
            new TenantMembership
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Role = TenantRole.TenantUser,
                AssignedAt = now,
                UpdatedAt = now,
            },
            ct);
    }

    private async Task<TenantSsoProviderRecord?> GetActiveProviderAsync(
        string tenantSlug,
        Guid providerId,
        bool asNoTracking,
        CancellationToken ct)
    {
        var query = dbContext.TenantSsoProviders
            .Include(record => record.Tenant)
            .Where(record => record.Id == providerId
                             && record.IsEnabled
                             && record.Tenant != null
                             && record.Tenant.Slug == tenantSlug
                             && record.Tenant.IsActive);

        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    private string ProtectChallengeState(string tenantSlug, Guid providerId, string callbackUrl, string? frontendReturnUrl)
    {
        var state = new TenantExternalChallengeState(
            tenantSlug,
            providerId,
            callbackUrl,
            frontendReturnUrl,
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"));

        return secretProtectionCodec.Protect(JsonSerializer.Serialize(state), ChallengeStatePurpose);
    }

    private TenantExternalChallengeState? TryValidateChallengeState(
        string? protectedState,
        string tenantSlug,
        string callbackUrl)
    {
        if (string.IsNullOrWhiteSpace(protectedState))
        {
            return null;
        }

        TenantExternalChallengeState? state;
        try
        {
            var json = secretProtectionCodec.Unprotect(protectedState, ChallengeStatePurpose);
            state = JsonSerializer.Deserialize<TenantExternalChallengeState>(json);
        }
        catch
        {
            return null;
        }

        if (state is null)
        {
            return null;
        }

        if (!string.Equals(state.TenantSlug, tenantSlug, StringComparison.Ordinal)
            || !string.Equals(state.CallbackUrl, callbackUrl, StringComparison.Ordinal))
        {
            return null;
        }

        return state.IssuedAtUtc + ChallengeStateLifetime >= DateTimeOffset.UtcNow
            ? state
            : null;
    }

    private async Task<TenantExternalIdentityPayload?> ResolveExternalIdentityPayloadAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string authorizationCode,
        string callbackUrl,
        CancellationToken ct)
    {
        return configuration.ProviderKind switch
        {
            TenantSsoProviderKind.Oidc => await this.ResolveOidcIdentityPayloadAsync(provider, configuration, authorizationCode, callbackUrl, ct),
            TenantSsoProviderKind.GitHub => await this.ResolveGitHubIdentityPayloadAsync(provider, configuration, authorizationCode, callbackUrl, ct),
            _ => null,
        };
    }

    private async Task<TenantExternalIdentityPayload?> ResolveOidcIdentityPayloadAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string authorizationCode,
        string callbackUrl,
        CancellationToken ct)
    {
        using var tokenDocument = await this.ExchangeAuthorizationCodeAsync(provider, configuration, authorizationCode, callbackUrl, ct);
        if (tokenDocument is null)
        {
            return null;
        }

        if (!TryGetString(tokenDocument.RootElement, "id_token", out var idToken))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "missing_id_token");
            return null;
        }

        JwtSecurityToken token;
        try
        {
            token = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
        }
        catch (ArgumentException)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "invalid_id_token");
            return null;
        }

        if (!token.Audiences.Contains(provider.ClientId, StringComparer.Ordinal))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "unexpected_token_audience");
            return null;
        }

        var subject = token.Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value;
        if (string.IsNullOrWhiteSpace(token.Issuer) || string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "missing_identity_claims");
            return null;
        }

        var email = token.Claims.FirstOrDefault(claim => claim.Type == "email")?.Value
                    ?? token.Claims.FirstOrDefault(claim => claim.Type == "preferred_username")?.Value
                    ?? token.Claims.FirstOrDefault(claim => claim.Type == "upn")?.Value;
        var emailVerified = TryParseBooleanClaim(token, "email_verified", out var explicitEmailVerified)
            ? explicitEmailVerified
            : IsImplicitlyVerifiedOidcEmail(provider, email);
        var displayName = token.Claims.FirstOrDefault(claim => claim.Type == "name")?.Value
                          ?? token.Claims.FirstOrDefault(claim => claim.Type == "preferred_username")?.Value;

        return new TenantExternalIdentityPayload(token.Issuer, subject, email, emailVerified, displayName);
    }

    private async Task<TenantExternalIdentityPayload?> ResolveGitHubIdentityPayloadAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string authorizationCode,
        string callbackUrl,
        CancellationToken ct)
    {
        using var tokenDocument = await this.ExchangeAuthorizationCodeAsync(provider, configuration, authorizationCode, callbackUrl, ct);
        if (tokenDocument is null)
        {
            return null;
        }

        if (!TryGetString(tokenDocument.RootElement, "access_token", out var accessToken))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "missing_access_token");
            return null;
        }

        using var userRequest = CreateGitHubApiRequest(configuration.UserEndpoint!, accessToken);
        using var userResponse = await httpClientFactory.CreateClient("TenantSsoAuth").SendAsync(userRequest, ct);
        if (!userResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} StatusCode={StatusCode}",
                provider.TenantId,
                provider.Id,
                "github_userinfo_exchange_failed",
                (int)userResponse.StatusCode);
            return null;
        }

        using var userDocument = await ReadJsonDocumentAsync(userResponse, ct);
        var subject = TryGetString(userDocument.RootElement, "id", out var idValue)
            ? idValue
            : null;

        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "missing_identity_claims");
            return null;
        }

        var displayName = TryGetString(userDocument.RootElement, "name", out var nameValue)
            ? nameValue
            : TryGetString(userDocument.RootElement, "login", out var loginValue)
                ? loginValue
                : null;

        var (email, emailVerified) = await this.ResolveGitHubEmailAsync(configuration.EmailsEndpoint!, accessToken, ct);
        return new TenantExternalIdentityPayload(configuration.Issuer, subject, email, emailVerified, displayName);
    }

    private async Task<(string? Email, bool EmailVerified)> ResolveGitHubEmailAsync(Uri emailsEndpoint, string accessToken, CancellationToken ct)
    {
        using var emailsRequest = CreateGitHubApiRequest(emailsEndpoint, accessToken);
        using var emailsResponse = await httpClientFactory.CreateClient("TenantSsoAuth").SendAsync(emailsRequest, ct);
        if (!emailsResponse.IsSuccessStatusCode)
        {
            return (null, false);
        }

        using var emailsDocument = await ReadJsonDocumentAsync(emailsResponse, ct);
        if (emailsDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (null, false);
        }

        string? fallbackVerifiedEmail = null;
        foreach (var emailEntry in emailsDocument.RootElement.EnumerateArray())
        {
            if (!TryGetString(emailEntry, "email", out var emailValue)
                || !TryGetBoolean(emailEntry, "verified", out var isVerified)
                || !isVerified)
            {
                continue;
            }

            if (TryGetBoolean(emailEntry, "primary", out var isPrimary) && isPrimary)
            {
                return (emailValue, true);
            }

            fallbackVerifiedEmail ??= emailValue;
        }

        return (fallbackVerifiedEmail, fallbackVerifiedEmail is not null);
    }

    private async Task<JsonDocument?> ExchangeAuthorizationCodeAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string authorizationCode,
        string callbackUrl,
        CancellationToken ct)
    {
        var clientSecret = this.TryUnprotectClientSecret(provider.ClientSecretProtected);
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "missing_client_secret");
            return null;
        }

        var formValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = provider.ClientId,
            ["client_secret"] = clientSecret,
            ["code"] = authorizationCode,
            ["redirect_uri"] = callbackUrl,
        };

        if (configuration.ProviderKind == TenantSsoProviderKind.Oidc)
        {
            formValues["grant_type"] = "authorization_code";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formValues),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (configuration.ProviderKind == TenantSsoProviderKind.GitHub)
        {
            request.Headers.UserAgent.ParseAdd("MeisterProPR-TenantAuth/1.0");
        }

        using var response = await httpClientFactory.CreateClient("TenantSsoAuth").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} StatusCode={StatusCode}",
                provider.TenantId,
                provider.Id,
                "token_exchange_failed",
                (int)response.StatusCode);
            return null;
        }

        return await ReadJsonDocumentAsync(response, ct);
    }

    private string? TryUnprotectClientSecret(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return null;
        }

        try
        {
            return secretProtectionCodec.Unprotect(protectedSecret, ClientSecretPurpose);
        }
        catch
        {
            return null;
        }
    }

    private async Task TouchExternalIdentityAsync(
        Guid tenantId,
        Guid providerId,
        TenantExternalIdentityPayload payload,
        CancellationToken ct)
    {
        var identity = await dbContext.ExternalIdentities
            .FirstOrDefaultAsync(
                record => record.TenantId == tenantId
                          && record.SsoProviderId == providerId
                          && record.Issuer == payload.Issuer
                          && record.Subject == payload.Subject,
                ct);

        if (identity is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.Email))
        {
            identity.Email = payload.Email.Trim();
        }

        identity.EmailVerified = payload.EmailVerified;
        identity.LastSignInAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task LinkExternalIdentityAsync(
        Guid tenantId,
        Guid providerId,
        Guid userId,
        TenantExternalIdentityPayload payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.Email))
        {
            return;
        }

        var email = payload.Email.Trim();
        var now = DateTimeOffset.UtcNow;
        var identity = await dbContext.ExternalIdentities
            .FirstOrDefaultAsync(
                record => record.TenantId == tenantId
                          && record.SsoProviderId == providerId
                          && record.Issuer == payload.Issuer
                          && record.Subject == payload.Subject,
                ct);

        if (identity is null)
        {
            dbContext.ExternalIdentities.Add(
                new ExternalIdentityRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = userId,
                    SsoProviderId = providerId,
                    Issuer = payload.Issuer,
                    Subject = payload.Subject,
                    Email = email,
                    EmailVerified = payload.EmailVerified,
                    CreatedAt = now,
                    LastSignInAt = now,
                });
        }
        else
        {
            identity.UserId = userId;
            identity.Email = email;
            identity.EmailVerified = payload.EmailVerified;
            identity.LastSignInAt = now;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<string> GenerateUniqueUsernameAsync(string email, CancellationToken ct)
    {
        var localPart = email.Split('@', 2)[0].Trim().ToLowerInvariant();
        var baseUsername = new string(
            localPart
                .Where(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-')
                .ToArray());

        if (string.IsNullOrWhiteSpace(baseUsername))
        {
            baseUsername = "user";
        }

        var candidate = baseUsername;
        var suffix = 1;
        while (await userRepository.GetByUsernameAsync(candidate, ct) is not null)
        {
            candidate = $"{baseUsername}{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string BuildCallbackUrl(Uri applicationBaseUri, string tenantSlug)
    {
        return new Uri(applicationBaseUri, $"auth/external/callback/{Uri.EscapeDataString(tenantSlug)}").ToString();
    }

    private static string BuildAuthorizationUrl(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string callbackUrl,
        string protectedState)
    {
        var queryParameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", provider.ClientId),
            new("redirect_uri", callbackUrl),
            new("scope", string.Join(' ', GetEffectiveScopes(provider, configuration.ProviderKind))),
            new("state", protectedState),
        };

        if (configuration.ProviderKind == TenantSsoProviderKind.Oidc)
        {
            queryParameters.Add(new KeyValuePair<string, string>("response_type", "code"));
        }

        if (NormalizeProviderKey(provider.ProviderKind) == "entraid")
        {
            queryParameters.Add(new KeyValuePair<string, string>("response_mode", "query"));
        }

        if (configuration.ProviderKind == TenantSsoProviderKind.GitHub)
        {
            queryParameters.Add(new KeyValuePair<string, string>("allow_signup", "false"));
        }

        return BuildUri(configuration.AuthorizationEndpoint, queryParameters).ToString();
    }

    private static TenantSsoProviderConfiguration? TryResolveProviderConfiguration(TenantSsoProviderRecord provider)
    {
        return (NormalizeProviderKey(provider.ProviderKind), NormalizeProtocolKey(provider.ProtocolKind)) switch
        {
            ("entraid", "oidc") => TryCreateEntraConfiguration(provider.IssuerOrAuthorityUrl),
            ("google", "oidc") => CreateGoogleConfiguration(),
            ("github", "oauth2") => CreateGitHubConfiguration(provider.IssuerOrAuthorityUrl),
            _ => null,
        };
    }

    private static TenantSsoProviderConfiguration? TryCreateEntraConfiguration(string? issuerOrAuthorityUrl)
    {
        if (!Uri.TryCreate(issuerOrAuthorityUrl, UriKind.Absolute, out var authority))
        {
            return null;
        }

        var builder = new UriBuilder(authority);
        var basePath = builder.Path.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = "/oauth2/v2.0";
        }
        else if (basePath.EndsWith("/oauth2/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            // already normalized
        }
        else if (basePath.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            basePath = $"{basePath[..^"/v2.0".Length]}/oauth2/v2.0";
        }
        else
        {
            basePath = $"{basePath}/oauth2/v2.0";
        }

        builder.Path = $"{basePath}/authorize";
        var authorizeEndpoint = builder.Uri;
        builder.Path = $"{basePath}/token";
        var tokenEndpoint = builder.Uri;

        return new TenantSsoProviderConfiguration(
            TenantSsoProviderKind.Oidc,
            authorizeEndpoint,
            tokenEndpoint,
            null,
            null,
            issuerOrAuthorityUrl!.TrimEnd('/'));
    }

    private static TenantSsoProviderConfiguration CreateGoogleConfiguration()
    {
        return new TenantSsoProviderConfiguration(
            TenantSsoProviderKind.Oidc,
            new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
            new Uri("https://oauth2.googleapis.com/token"),
            null,
            null,
            "https://accounts.google.com");
    }

    private static TenantSsoProviderConfiguration CreateGitHubConfiguration(string? issuerOrAuthorityUrl)
    {
        var authority = Uri.TryCreate(issuerOrAuthorityUrl, UriKind.Absolute, out var configuredAuthority)
            ? configuredAuthority
            : new Uri("https://github.com/");

        var normalizedAuthority = EnsureTrailingSlash(authority);
        var apiBase = string.Equals(normalizedAuthority.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? new Uri("https://api.github.com/")
            : new Uri(normalizedAuthority, "api/v3/");

        return new TenantSsoProviderConfiguration(
            TenantSsoProviderKind.GitHub,
            new Uri(normalizedAuthority, "login/oauth/authorize"),
            new Uri(normalizedAuthority, "login/oauth/access_token"),
            new Uri(apiBase, "user"),
            new Uri(apiBase, "user/emails"),
            normalizedAuthority.GetLeftPart(UriPartial.Authority));
    }

    private static HttpRequestMessage CreateGitHubApiRequest(Uri endpoint, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("MeisterProPR-TenantAuth/1.0");
        return request;
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(content);
    }

    private static Uri BuildUri(Uri baseUri, IEnumerable<KeyValuePair<string, string>> queryParameters)
    {
        var builder = new UriBuilder(baseUri);
        var existingQuery = builder.Query;
        var query = string.Join(
            "&",
            queryParameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        builder.Query = string.IsNullOrWhiteSpace(existingQuery)
            ? query
            : $"{existingQuery.TrimStart('?')}&{query}";
        return builder.Uri;
    }

    private static IReadOnlyList<string> GetEffectiveScopes(TenantSsoProviderRecord provider, TenantSsoProviderKind providerKind)
    {
        if (provider.Scopes.Length > 0)
        {
            return provider.Scopes;
        }

        return providerKind == TenantSsoProviderKind.GitHub
            ? ["read:user", "user:email"]
            : ["openid", "profile", "email"];
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseBooleanClaim(JwtSecurityToken token, string claimType, out bool value)
    {
        value = false;
        var claimValue = token.Claims.FirstOrDefault(claim => claim.Type == claimType)?.Value;
        if (!bool.TryParse(claimValue, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool IsImplicitlyVerifiedOidcEmail(TenantSsoProviderRecord provider, string? email)
    {
        return NormalizeProviderKey(provider.ProviderKind) == "entraid"
               && !string.IsNullOrWhiteSpace(email)
               && GetEmailDomain(email) is not null;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Path = $"{uri.AbsolutePath.TrimEnd('/')}/",
        };

        return builder.Uri;
    }

    private static string NormalizeProviderKey(string providerKind)
    {
        return providerKind.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string NormalizeProtocolKey(string protocolKind)
    {
        return protocolKind.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string GetProviderLabel(string providerKind)
    {
        return NormalizeProviderKey(providerKind) switch
        {
            "entraid" => "Microsoft",
            "github" => "GitHub",
            "google" => "Google",
            _ => providerKind,
        };
    }

    private static string? GetEmailDomain(string email)
    {
        var separatorIndex = email.LastIndexOf('@');
        if (separatorIndex <= 0 || separatorIndex == email.Length - 1)
        {
            return null;
        }

        return email[(separatorIndex + 1)..].Trim().ToLowerInvariant();
    }

    private static TenantExternalSignInCompletionResult Failure(
        string failureCode,
        string message,
        string? frontendReturnUrl = null)
    {
        return new TenantExternalSignInCompletionResult(null, frontendReturnUrl, failureCode, message);
    }

    private sealed record TenantExternalChallengeState(
        string TenantSlug,
        Guid ProviderId,
        string CallbackUrl,
        string? FrontendReturnUrl,
        DateTimeOffset IssuedAtUtc,
        string Nonce);

    private sealed record TenantSsoProviderConfiguration(
        TenantSsoProviderKind ProviderKind,
        Uri AuthorizationEndpoint,
        Uri TokenEndpoint,
        Uri? UserEndpoint,
        Uri? EmailsEndpoint,
        string Issuer);

    private enum TenantSsoProviderKind
    {
        Oidc,
        GitHub,
    }
}
