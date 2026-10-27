// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>Tenant-aware authentication service for local and external sign-in flows.</summary>
/// <remarks>
///     <para>
///         The external sign-in uses the authorization code flow with PKCE (S256) for every provider kind, and a nonce
///         for the OpenID Connect kinds. The state, the nonce and the code verifier stay in a protected httpOnly cookie
///         of the browser that started the sign-in. The callback completes a sign-in only with that cookie and accepts
///         each state once.
///     </para>
///     <para>
///         An external identity, the pair of issuer and subject, reaches an account through a link of the identity to the
///         account. The email address that the identity provider states never selects an existing account. The
///         administrator of a tenant configures the providers of the tenant and can make one of them state any address,
///         and an account reached that way would bring the memberships of all its tenants with it.
///     </para>
///     <para>
///         Without a link, a sign-in reaches an existing account in one case: a sign-in through another provider with the
///         same issuer proved the same subject for that account. The issuer of an OpenID Connect provider must be the
///         authority that the provider is configured with, so the keys that signed the ID token are the keys of that
///         authority. The issuer of a GitHub provider is the host that answered for the user. The administrator of
///         another tenant can state neither.
///     </para>
///     <para>
///         When no link applies and an account with the stated email address exists, the sign-in keeps the identity as a
///         pending link. The person either signs in to the existing account and confirms the link there, or continues
///         with a new account. Without such an account, the sign-in creates one when the provider admits new members.
///     </para>
/// </remarks>
public sealed class TenantAuthService(
    MeisterProPRDbContext dbContext,
    IUserRepository userRepository,
    IPasswordHashService passwordHashService,
    ISecretProtectionCodec secretProtectionCodec,
    IHttpClientFactory httpClientFactory,
    IOidcTokenValidator oidcTokenValidator,
    IAccountLockoutService accountLockoutService,
    ExternalSignInRedemptions redemptions,
    ILogger<TenantAuthService> logger) : ITenantAuthService
{
    /// <summary>How long a pending link waits for its confirmation or for a new account.</summary>
    public static readonly TimeSpan PendingLinkLifetime = TimeSpan.FromMinutes(15);

    private const string ClientSecretPurpose = "tenant-sso-provider-client-secret";
    private const string FlowPurpose = "tenant-sso-sign-in-flow";

    /// <summary>The purpose that protects a pending link.</summary>
    internal const string PendingLinkPurpose = "tenant-sso-pending-link";

    private const string LinkConfirmationRequired = "link_confirmation_required";

    private const string LinkConfirmationRequiredMessage =
        "An account with the email address of this sign-in already exists. Sign in to that account to link this "
        + "sign-in to it, or continue with a new account.";

    private const string InvalidStateMessage = "The tenant sign-in request is invalid or has expired. Start the sign-in again.";

    private const string AutoCreateDisabledMessage =
        "First-time sign-in is disabled for this provider. Ask a tenant administrator to enable sign-in for your account.";

    // GitHub refuses API requests without a user agent.
    private const string GitHubUserAgent = "MeisterDev.ProPR-TenantAuth/1.0";

    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(15);

    // The hosts at which Microsoft Entra ID serves the discovery documents of its directories.
    private static readonly string[] MicrosoftSignInHosts =
    [
        "login.microsoftonline.com",
        "login.microsoftonline.us",
        "login.partner.microsoftonline.cn",
    ];

    // The issuer of the version 2.0 tokens of an Entra directory: the directory ID, then v2.0.
    private static readonly Regex EntraDirectoryIssuerPath = new(
        "^/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/v2\\.0/?$",
        RegexOptions.CultureInvariant);

    public async Task<TenantLoginOptionsDto?> GetLoginOptionsAsync(string tenantSlug, CancellationToken ct = default)
    {
        var safeTenantSlug = tenantSlug.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.Slug == tenantSlug, ct);

        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning(
                "TenantLoginOptionsUnavailable TenantSlug={TenantSlug} Reason={Reason}",
                safeTenantSlug,
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
            safeTenantSlug,
            tenant.LocalLoginEnabled,
            providerOptions.Count);

        return new TenantLoginOptionsDto(tenant.Slug, tenant.LocalLoginEnabled, providerOptions);
    }

    public async Task<AppUser?> AuthenticateLocalAsync(string tenantSlug, string username, string password, CancellationToken ct = default)
    {
        var safeTenantSlug = tenantSlug.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var safeUsername = username.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.Slug == tenantSlug, ct);

        if (tenant is null || !tenant.IsActive || !tenant.LocalLoginEnabled)
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                safeTenantSlug,
                safeUsername,
                DetermineTenantLoginDenialReason(tenant));
            return null;
        }

        var user = await userRepository.GetByUsernameAsync(username, ct);
        if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                safeTenantSlug,
                safeUsername,
                DetermineUserLoginDenialReason(user));
            return null;
        }

        if (accountLockoutService.IsLockedOut(user))
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                safeTenantSlug,
                safeUsername,
                "account_locked");
            return null;
        }

        if (!passwordHashService.Verify(password, user.PasswordHash))
        {
            await accountLockoutService.RecordFailureAsync(user, ct);
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                safeTenantSlug,
                safeUsername,
                "invalid_password");
            return null;
        }

        await accountLockoutService.ResetAsync(user, ct);

        var membership = await userRepository.GetTenantMembershipAsync(tenant.Id, user.Id, ct);
        if (membership is null)
        {
            logger.LogWarning(
                "TenantLocalLoginDenied TenantSlug={TenantSlug} Username={Username} Reason={Reason}",
                safeTenantSlug,
                safeUsername,
                "missing_tenant_membership");
            return null;
        }

        logger.LogInformation(
            "TenantLocalLoginSucceeded TenantSlug={TenantSlug} UserId={UserId}",
            safeTenantSlug,
            user.Id);
        return user;
    }

    public async Task<TenantExternalChallengeResult?> BuildExternalChallengeAsync(
        string tenantSlug,
        Guid providerId,
        Uri applicationBaseUri,
        Uri? frontendReturnUrl = null,
        TenantExternalSignInMode mode = TenantExternalSignInMode.SignIn,
        CancellationToken ct = default)
    {
        var safeTenantSlug = tenantSlug.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var provider = await this.GetActiveProviderAsync(tenantSlug, providerId, true, ct);
        if (provider is null)
        {
            logger.LogWarning(
                "TenantExternalChallengeUnavailable TenantSlug={TenantSlug} ProviderId={ProviderId}",
                safeTenantSlug,
                providerId);
            return null;
        }

        var providerConfiguration = TryResolveProviderConfiguration(provider);
        if (providerConfiguration is null)
        {
            logger.LogWarning(
                "TenantExternalChallengeUnavailable TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason}",
                safeTenantSlug,
                providerId,
                "unsupported_provider_configuration");
            return null;
        }

        var callbackUrl = BuildCallbackUrl(applicationBaseUri, tenantSlug);
        var flow = new TenantSignInFlow(
            AuthorizationCodeFlow.CreateRandomValue(),
            AuthorizationCodeFlow.CreateRandomValue(),
            AuthorizationCodeFlow.CreateRandomValue(),
            tenantSlug,
            providerId,
            callbackUrl,
            frontendReturnUrl?.ToString(),
            mode,
            DateTimeOffset.UtcNow);
        var redirectUrl = BuildAuthorizationUrl(provider, providerConfiguration, flow);
        var safeCallbackUrl = callbackUrl.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        logger.LogInformation(
            "TenantExternalChallengeBuilt TenantSlug={TenantSlug} ProviderId={ProviderId} CallbackUrl={CallbackUrl} Mode={Mode}",
            safeTenantSlug,
            providerId,
            safeCallbackUrl,
            mode);

        return new TenantExternalChallengeResult(
            redirectUrl,
            providerConfiguration.AuthorizationEndpoint.GetLeftPart(UriPartial.Authority),
            ProtectedSignInState.Protect(secretProtectionCodec, flow, FlowPurpose));
    }

    public async Task<TenantExternalSignInCompletionResult> CompleteExternalSignInAsync(
        string tenantSlug,
        Uri applicationBaseUri,
        TenantExternalCallbackRequest callback,
        string? protectedFlow,
        CancellationToken ct = default)
    {
        var safeTenantSlug = tenantSlug.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var callbackUrl = BuildCallbackUrl(applicationBaseUri, tenantSlug);
        var flow = ProtectedSignInState.TryRead<TenantSignInFlow>(secretProtectionCodec, protectedFlow, FlowPurpose);
        if (flow is null
            || !string.Equals(flow.State, callback.State, StringComparison.Ordinal)
            || !string.Equals(flow.TenantSlug, tenantSlug, StringComparison.Ordinal)
            || !string.Equals(flow.CallbackUrl, callbackUrl, StringComparison.Ordinal)
            || flow.IssuedAtUtc + FlowLifetime < DateTimeOffset.UtcNow)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} Reason={Reason}",
                safeTenantSlug,
                flow is null ? "missing_or_invalid_flow" : "invalid_state");
            return Failure("invalid_state", InvalidStateMessage, flow?.FrontendReturnUrl);
        }

        // The state is accepted once. A second callback with the same state and cookie, sent again by the browser or by
        // someone who copied both, is refused before ProPR asks the provider.
        if (!await redemptions.TryRedeemAsync(flow.State, flow.IssuedAtUtc + FlowLifetime, ct))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} Reason={Reason}",
                safeTenantSlug,
                "state_already_used");
            return Failure("invalid_state", InvalidStateMessage, flow.FrontendReturnUrl);
        }

        var frontendReturnUrl = flow.FrontendReturnUrl;
        var providerId = flow.ProviderId;

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            // The description comes from the provider, which the administrator of the tenant configures, so it goes to
            // the log only. The person reads a fixed message for the error code.
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason} ProviderError={ProviderError} ProviderErrorDescription={ProviderErrorDescription}",
                safeTenantSlug,
                providerId,
                "provider_error",
                ForLog(callback.Error),
                ForLog(callback.ErrorDescription));
            return Failure("provider_error", DescribeProviderError(callback.Error), frontendReturnUrl);
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantSlug={TenantSlug} ProviderId={ProviderId} Reason={Reason}",
                safeTenantSlug,
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
                safeTenantSlug,
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
                safeTenantSlug,
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
            flow,
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
            if (flow.Mode == TenantExternalSignInMode.Link)
            {
                // A sign-in started from the settings of an account signs nobody in. It keeps the identity as a pending
                // link, which the account that is signed in when the person confirms it receives.
                this.EnsureIdentityMayBeLinked(provider, payload);
                return this.KeepPendingLink(provider, payload, TenantExternalSignInMode.Link, frontendReturnUrl);
            }

            return await this.SignInExternalIdentityAsync(provider, providerConfiguration.ProviderKind, payload, frontendReturnUrl, ct);
        }
        catch (TenantExternalSignInPolicyException exception)
        {
            return Failure(exception.FailureCode, exception.Message, frontendReturnUrl);
        }
    }

    public async Task<TenantPendingLinkDto?> GetPendingLinkAsync(string? protectedPendingLink, CancellationToken ct = default)
    {
        var pendingLink = await this.ReadPendingLinkAsync(protectedPendingLink, ct);
        if (pendingLink is null)
        {
            return null;
        }

        var provider = await dbContext.TenantSsoProviders
            .AsNoTracking()
            .Include(record => record.Tenant)
            .FirstOrDefaultAsync(record => record.Id == pendingLink.ProviderId && record.TenantId == pendingLink.TenantId, ct);
        if (provider?.Tenant is null)
        {
            return null;
        }

        return new TenantPendingLinkDto(
            provider.Tenant.Slug,
            provider.Tenant.DisplayName,
            provider.DisplayName,
            GetProviderLabel(provider.ProviderKind),
            pendingLink.Email,
            pendingLink.Mode == TenantExternalSignInMode.SignIn && provider.AutoCreateUsers,
            pendingLink.IssuedAtUtc + PendingLinkLifetime);
    }

    public async Task<TenantPendingLinkOutcome> ConfirmPendingLinkAsync(
        string? protectedPendingLink,
        Guid userId,
        CancellationToken ct = default)
    {
        var pendingLink = await this.ReadPendingLinkAsync(protectedPendingLink, ct);
        if (pendingLink is null)
        {
            return PendingLinkNotFound();
        }

        var provider = await this.GetActiveProviderAsync(pendingLink.TenantId, pendingLink.ProviderId, ct);
        if (provider?.Tenant is null)
        {
            return PendingLinkRefused("tenant_or_provider_inactive", "The tenant sign-in provider is unavailable.");
        }

        var policyRefusal = this.RefuseByCurrentPolicy(provider, pendingLink);
        if (policyRefusal is not null)
        {
            return policyRefusal;
        }

        var account = await userRepository.GetByIdAsync(userId, ct);
        if (account is null || !account.IsActive)
        {
            return PendingLinkRefused("account_disabled", "The account that is signed in is disabled.");
        }

        // The account of a platform administrator holds rights over the whole installation. A link would let the
        // provider of a tenant, which the administrator of that tenant configures, sign in to it.
        if (account.GlobalRole == AppUserRole.Admin)
        {
            this.LogPendingLinkRefused(pendingLink, "platform_administrator_not_linkable", account.Id);
            return PendingLinkRefused(
                "platform_administrator_not_linkable",
                "ProPR does not link a sign-in of a tenant to the account of a platform administrator. Sign in to the "
                + "tenant with a separate account.");
        }

        var existingLink = await dbContext.ExternalIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(
                link => link.TenantId == pendingLink.TenantId
                        && link.SsoProviderId == pendingLink.ProviderId
                        && link.Issuer == pendingLink.Issuer
                        && link.Subject == pendingLink.Subject,
                ct);
        if (existingLink is not null && existingLink.UserId != account.Id)
        {
            this.LogPendingLinkRefused(pendingLink, "identity_already_linked", account.Id);
            return PendingLinkRefused("identity_already_linked", "This sign-in is already linked to another account.");
        }

        var hasMembership = await userRepository.GetTenantMembershipAsync(provider.TenantId, account.Id, ct) is not null;
        if (existingLink is null && !hasMembership && !provider.AutoCreateUsers)
        {
            this.LogPendingLinkRefused(pendingLink, "auto_create_disabled", account.Id);
            return PendingLinkRefused("auto_create_disabled", AutoCreateDisabledMessage);
        }

        await using var transaction = await this.BeginPendingLinkTransactionAsync(ct);
        if (!await redemptions.TryRedeemAsync(pendingLink.Id, pendingLink.IssuedAtUtc + PendingLinkLifetime, ct))
        {
            return PendingLinkNotFound();
        }

        if (existingLink is null)
        {
            if (!hasMembership)
            {
                await this.EnsureTenantMembershipAsync(provider.TenantId, account.Id, ct);
            }

            await this.LinkExternalIdentityAsync(provider.TenantId, provider.Id, account.Id, pendingLink.ToPayload(), ct);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        logger.LogInformation(
            "TenantExternalIdentityLinkConfirmed TenantId={TenantId} ProviderId={ProviderId} UserId={UserId}",
            provider.TenantId,
            provider.Id,
            account.Id);
        return new TenantPendingLinkOutcome(account, provider.Tenant.Slug);
    }

    public async Task<TenantPendingLinkOutcome> CreateAccountForPendingLinkAsync(
        string? protectedPendingLink,
        CancellationToken ct = default)
    {
        var pendingLink = await this.ReadPendingLinkAsync(protectedPendingLink, ct);
        if (pendingLink is null)
        {
            return PendingLinkNotFound();
        }

        if (pendingLink.Mode == TenantExternalSignInMode.Link)
        {
            return PendingLinkRefused(
                "account_creation_unavailable",
                "ProPR creates no account for a sign-in that started from the settings of an account.");
        }

        var provider = await this.GetActiveProviderAsync(pendingLink.TenantId, pendingLink.ProviderId, ct);
        if (provider?.Tenant is null)
        {
            return PendingLinkRefused("tenant_or_provider_inactive", "The tenant sign-in provider is unavailable.");
        }

        if (!provider.AutoCreateUsers)
        {
            this.LogPendingLinkRefused(pendingLink, "auto_create_disabled", null);
            return PendingLinkRefused("auto_create_disabled", AutoCreateDisabledMessage);
        }

        var policyRefusal = this.RefuseByCurrentPolicy(provider, pendingLink);
        if (policyRefusal is not null)
        {
            return policyRefusal;
        }

        var linked = await dbContext.ExternalIdentities.AnyAsync(
            link => link.TenantId == pendingLink.TenantId
                    && link.SsoProviderId == pendingLink.ProviderId
                    && link.Issuer == pendingLink.Issuer
                    && link.Subject == pendingLink.Subject,
            ct);
        if (linked)
        {
            this.LogPendingLinkRefused(pendingLink, "identity_already_linked", null);
            return PendingLinkRefused("identity_already_linked", "This sign-in is already linked to another account.");
        }

        await using var transaction = await this.BeginPendingLinkTransactionAsync(ct);
        if (!await redemptions.TryRedeemAsync(pendingLink.Id, pendingLink.IssuedAtUtc + PendingLinkLifetime, ct))
        {
            return PendingLinkNotFound();
        }

        var account = await this.ProvisionExternalUserAsync(
            provider,
            pendingLink.Email,
            pendingLink.Email.ToUpperInvariant(),
            pendingLink.ToPayload(),
            ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new TenantPendingLinkOutcome(account, provider.Tenant.Slug);
    }

    public async Task<IReadOnlyList<TenantSignInLinkDto>> ListSignInLinksAsync(Guid userId, CancellationToken ct = default)
    {
        var links = await dbContext.ExternalIdentities
            .AsNoTracking()
            .Where(link => link.UserId == userId && link.Tenant != null && link.SsoProvider != null)
            .Select(link => new
            {
                link.Tenant!.Slug,
                TenantDisplayName = link.Tenant.DisplayName,
                ProviderDisplayName = link.SsoProvider!.DisplayName,
                link.SsoProvider.ProviderKind,
                link.Email,
                link.CreatedAt,
                link.LastSignInAt,
            })
            .ToListAsync(ct);

        return links
            .OrderBy(link => link.TenantDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(link => link.ProviderDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(link => new TenantSignInLinkDto(
                link.Slug,
                link.TenantDisplayName,
                link.ProviderDisplayName,
                GetProviderLabel(link.ProviderKind),
                link.Email,
                link.CreatedAt,
                link.LastSignInAt))
            .ToList();
    }

    private static string DetermineTenantLoginDenialReason(TenantRecord? tenant)
    {
        if (tenant is null)
        {
            return "tenant_not_found";
        }

        return !tenant.IsActive ? "tenant_inactive" : "local_login_disabled";
    }

    private static string DetermineUserLoginDenialReason(AppUser? user)
    {
        if (user is null)
        {
            return "user_not_found";
        }

        return !user.IsActive ? "user_inactive" : "missing_password_hash";
    }

    private async Task<TenantExternalSignInCompletionResult> SignInExternalIdentityAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderKind providerKind,
        TenantExternalIdentityPayload payload,
        string? frontendReturnUrl,
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
            return SignedIn(await this.CompleteLinkedExternalSignInAsync(provider, linkedUser, payload, ct), frontendReturnUrl);
        }

        var email = this.EnsureIdentityMayBeLinked(provider, payload);

        var accountOfIssuer = await this.FindAccountOfVerifiedIssuerAsync(provider, providerKind, payload, ct);
        if (accountOfIssuer is not null)
        {
            return SignedIn(await this.LinkAccountOfVerifiedIssuerAsync(provider, accountOfIssuer, payload, ct), frontendReturnUrl);
        }

        var normalizedEmail = email.ToUpperInvariant();
        if (await dbContext.AppUsers.AnyAsync(user => user.NormalizedEmail == normalizedEmail, ct))
        {
            logger.LogInformation(
                "TenantExternalSignInLinkConfirmationRequired TenantId={TenantId} ProviderId={ProviderId}",
                provider.TenantId,
                provider.Id);
            return this.KeepPendingLink(provider, payload, TenantExternalSignInMode.SignIn, frontendReturnUrl);
        }

        return SignedIn(await this.ProvisionExternalUserAsync(provider, email, normalizedEmail, payload, ct), frontendReturnUrl);
    }

    private async Task<AppUser?> CompleteLinkedExternalSignInAsync(
        TenantSsoProviderRecord provider,
        AppUser linkedUser,
        TenantExternalIdentityPayload payload,
        CancellationToken ct)
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

    /// <summary>
    ///     Returns the account that a link of another provider holds for the same issuer and subject, when a sign-in
    ///     proved that link and the provider is of the same kind. Links that no sign-in proved and accounts of platform
    ///     administrators do not count, and when the links name more than one account, none is returned.
    /// </summary>
    private async Task<AppUser?> FindAccountOfVerifiedIssuerAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderKind providerKind,
        TenantExternalIdentityPayload payload,
        CancellationToken ct)
    {
        var candidates = await dbContext.ExternalIdentities
            .AsNoTracking()
            .Where(link => link.TenantId != null
                           && link.SsoProviderId != provider.Id
                           && link.IssuerVerified
                           && link.Issuer == payload.Issuer
                           && link.Subject == payload.Subject)
            .Select(link => new { link.UserId, link.SsoProvider })
            .ToListAsync(ct);

        var userIds = candidates
            .Where(candidate => candidate.SsoProvider is not null
                                && TryResolveProviderConfiguration(candidate.SsoProvider)?.ProviderKind == providerKind)
            .Select(candidate => candidate.UserId)
            .Distinct()
            .ToList();

        if (userIds.Count != 1)
        {
            if (userIds.Count > 1)
            {
                logger.LogWarning(
                    "TenantExternalIdentityLinkedToSeveralAccounts TenantId={TenantId} ProviderId={ProviderId} AccountCount={AccountCount}",
                    provider.TenantId,
                    provider.Id,
                    userIds.Count);
            }

            return null;
        }

        var account = await userRepository.GetByIdAsync(userIds[0], ct);
        return account is { GlobalRole: AppUserRole.User } ? account : null;
    }

    private async Task<AppUser?> LinkAccountOfVerifiedIssuerAsync(
        TenantSsoProviderRecord provider,
        AppUser account,
        TenantExternalIdentityPayload payload,
        CancellationToken ct)
    {
        if (!account.IsActive)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} UserId={UserId}",
                provider.TenantId,
                provider.Id,
                "account_disabled",
                account.Id);
            return null;
        }

        if (await userRepository.GetTenantMembershipAsync(provider.TenantId, account.Id, ct) is null)
        {
            if (!provider.AutoCreateUsers)
            {
                throw this.AutoCreateDisabled(provider);
            }

            await this.EnsureTenantMembershipAsync(provider.TenantId, account.Id, ct);
        }

        await this.LinkExternalIdentityAsync(provider.TenantId, provider.Id, account.Id, payload, ct);
        logger.LogInformation(
            "TenantExternalIdentityLinkedThroughIssuer TenantId={TenantId} ProviderId={ProviderId} UserId={UserId}",
            provider.TenantId,
            provider.Id,
            account.Id);
        return account;
    }

    /// <summary>
    ///     Applies the policy of the provider to an identity that is about to receive a link: the provider must state a
    ///     verified email address in an allowed domain.
    /// </summary>
    /// <returns>The email address of the identity.</returns>
    private string EnsureIdentityMayBeLinked(TenantSsoProviderRecord provider, TenantExternalIdentityPayload payload)
    {
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
        var domain = GetEmailDomain(email);
        if (domain is null)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "invalid_email");

            throw new TenantExternalSignInPolicyException(
                "external_identity_rejected",
                "External identity rejected by tenant policy.");
        }

        if (provider.AllowedEmailDomains.Length == 0
            || provider.AllowedEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            return email;
        }

        logger.LogWarning(
            "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
            provider.TenantId,
            provider.Id,
            "disallowed_domain");

        throw new TenantExternalSignInPolicyException(
            "disallowed_domain",
            $"Email domain '{domain}' is not allowed for this tenant sign-in provider.");
    }

    private TenantExternalSignInPolicyException AutoCreateDisabled(TenantSsoProviderRecord provider)
    {
        logger.LogWarning(
            "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
            provider.TenantId,
            provider.Id,
            "auto_create_disabled");

        return new TenantExternalSignInPolicyException("auto_create_disabled", AutoCreateDisabledMessage);
    }

    /// <summary>
    ///     Keeps the identity as a pending link. The callback hands it to the browser in a protected cookie, and the
    ///     person confirms the link from an account. After a sign-in that started on the sign-in page, the person can
    ///     continue with a new account instead, when the provider admits new members.
    /// </summary>
    private TenantExternalSignInCompletionResult KeepPendingLink(
        TenantSsoProviderRecord provider,
        TenantExternalIdentityPayload payload,
        TenantExternalSignInMode mode,
        string? frontendReturnUrl)
    {
        var pendingLink = new TenantPendingLink(
            AuthorizationCodeFlow.CreateRandomValue(),
            provider.TenantId,
            provider.Id,
            payload.Issuer,
            payload.Subject,
            payload.Email!.Trim(),
            payload.EmailVerified,
            mode,
            DateTimeOffset.UtcNow);

        return new TenantExternalSignInCompletionResult(
            null,
            frontendReturnUrl,
            LinkConfirmationRequired,
            LinkConfirmationRequiredMessage,
            ProtectedSignInState.Protect(secretProtectionCodec, pendingLink, PendingLinkPurpose));
    }

    /// <summary>
    ///     Applies the policy of the provider as it is now to the identity of a pending link. A tenant administrator can
    ///     restrict the allowed email domains while the sign-in waits, and later sign-ins through the link do not check
    ///     the domains again.
    /// </summary>
    /// <returns>The refusal, or null when the policy admits the identity.</returns>
    private TenantPendingLinkOutcome? RefuseByCurrentPolicy(TenantSsoProviderRecord provider, TenantPendingLink pendingLink)
    {
        try
        {
            this.EnsureIdentityMayBeLinked(provider, pendingLink.ToPayload());
            return null;
        }
        catch (TenantExternalSignInPolicyException exception)
        {
            this.LogPendingLinkRefused(pendingLink, exception.FailureCode, null);
            return PendingLinkRefused(exception.FailureCode, exception.Message);
        }
    }

    /// <summary>
    ///     Starts the transaction in which a pending link is used: its redemption and the account, membership and link
    ///     that follow are written together or not at all. A failure then leaves the pending link usable and no part of
    ///     an account behind. A store without transactions, such as the in-memory store, writes the steps one after
    ///     another.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginPendingLinkTransactionAsync(CancellationToken ct)
    {
        return dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(ct) : null;
    }

    /// <summary>Returns the pending link of the protected value, or null when it is altered, expired or used.</summary>
    private async Task<TenantPendingLink?> ReadPendingLinkAsync(string? protectedPendingLink, CancellationToken ct)
    {
        var pendingLink = ProtectedSignInState.TryRead<TenantPendingLink>(secretProtectionCodec, protectedPendingLink, PendingLinkPurpose);
        if (pendingLink is null || pendingLink.IssuedAtUtc + PendingLinkLifetime < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return await redemptions.IsRedeemedAsync(pendingLink.Id, ct) ? null : pendingLink;
    }

    private void LogPendingLinkRefused(TenantPendingLink pendingLink, string reason, Guid? userId)
    {
        logger.LogWarning(
            "TenantExternalIdentityLinkRefused TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} UserId={UserId}",
            pendingLink.TenantId,
            pendingLink.ProviderId,
            reason,
            userId);
    }

    private async Task<AppUser> ProvisionExternalUserAsync(
        TenantSsoProviderRecord provider,
        string email,
        string normalizedEmail,
        TenantExternalIdentityPayload payload,
        CancellationToken ct)
    {
        if (!provider.AutoCreateUsers)
        {
            throw this.AutoCreateDisabled(provider);
        }

        var now = DateTimeOffset.UtcNow;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = await ExternalAccountNames.CreateUniqueUsernameAsync(userRepository, email, ct),
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
            "TenantExternalUserProvisioned TenantId={TenantId} ProviderId={ProviderId} UserId={UserId}",
            provider.TenantId,
            provider.Id,
            user.Id);

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

    private Task<TenantSsoProviderRecord?> GetActiveProviderAsync(Guid tenantId, Guid providerId, CancellationToken ct)
    {
        return dbContext.TenantSsoProviders
            .AsNoTracking()
            .Include(record => record.Tenant)
            .FirstOrDefaultAsync(
                record => record.Id == providerId
                          && record.TenantId == tenantId
                          && record.IsEnabled
                          && record.Tenant != null
                          && record.Tenant.IsActive,
                ct);
    }

    private async Task<TenantExternalIdentityPayload?> ResolveExternalIdentityPayloadAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string authorizationCode,
        TenantSignInFlow flow,
        CancellationToken ct)
    {
        return configuration.ProviderKind switch
        {
            TenantSsoProviderKind.Oidc => await this.ResolveOidcIdentityPayloadAsync(provider, configuration, authorizationCode, flow, ct),
            TenantSsoProviderKind.GitHub => await this.ResolveGitHubIdentityPayloadAsync(provider, configuration, authorizationCode, flow, ct),
            _ => null,
        };
    }

    private async Task<TenantExternalIdentityPayload?> ResolveOidcIdentityPayloadAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string authorizationCode,
        TenantSignInFlow flow,
        CancellationToken ct)
    {
        using var tokenDocument = await this.ExchangeAuthorizationCodeAsync(provider, configuration, authorizationCode, flow, ct);
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

        var metadataAddress = BuildOidcMetadataAddress(configuration.Issuer);
        var validation = await oidcTokenValidator
            .ValidateAsync(new OidcTokenValidationRequest(idToken, metadataAddress, provider.ClientId, flow.Nonce), ct)
            .ConfigureAwait(false);
        if (!validation.IsValid || validation.Principal is null || validation.Issuer is null)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                validation.FailureCode ?? "invalid_id_token");
            return null;
        }

        // The validator takes the signing keys and the issuer from the discovery document at the configured authority,
        // so a server there could sign tokens in the name of any issuer. The issuer counts only when it is the authority.
        if (!IsIssuerOfAuthority(validation.Issuer, configuration.Issuer, IsEntraProvider(provider)))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} Issuer={Issuer}",
                provider.TenantId,
                provider.Id,
                "issuer_not_authority",
                validation.Issuer.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
            return null;
        }

        var claims = validation.Principal.Claims.ToList();
        var subject = claims.FirstOrDefault(claim => claim.Type == "sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason}",
                provider.TenantId,
                provider.Id,
                "missing_identity_claims");
            return null;
        }

        var email = claims.FirstOrDefault(claim => claim.Type == "email")?.Value
                    ?? claims.FirstOrDefault(claim => claim.Type == "preferred_username")?.Value
                    ?? claims.FirstOrDefault(claim => claim.Type == "upn")?.Value;
        var emailVerified = TryParseBooleanClaim(claims, "email_verified", out var explicitEmailVerified)
            ? explicitEmailVerified
            : IsImplicitlyVerifiedOidcEmail(provider, email);
        var displayName = claims.FirstOrDefault(claim => claim.Type == "name")?.Value
                          ?? claims.FirstOrDefault(claim => claim.Type == "preferred_username")?.Value;

        return new TenantExternalIdentityPayload(validation.Issuer, subject, email, emailVerified, displayName);
    }

    private async Task<TenantExternalIdentityPayload?> ResolveGitHubIdentityPayloadAsync(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        string authorizationCode,
        TenantSignInFlow flow,
        CancellationToken ct)
    {
        using var tokenDocument = await this.ExchangeAuthorizationCodeAsync(provider, configuration, authorizationCode, flow, ct);
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
        using var userResponse = await httpClientFactory.CreateClient(AuthorizationCodeFlow.HttpClientName).SendAsync(userRequest, ct);
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

        var fallbackDisplayName = TryGetString(userDocument.RootElement, "login", out var loginValue) ? loginValue : null;
        var displayName = TryGetString(userDocument.RootElement, "name", out var nameValue) ? nameValue : fallbackDisplayName;

        var (email, emailVerified) = await this.ResolveGitHubEmailAsync(configuration.EmailsEndpoint!, accessToken, ct);
        return new TenantExternalIdentityPayload(configuration.Issuer, subject, email, emailVerified, displayName);
    }

    private async Task<(string? Email, bool EmailVerified)> ResolveGitHubEmailAsync(Uri emailsEndpoint, string accessToken, CancellationToken ct)
    {
        using var emailsRequest = CreateGitHubApiRequest(emailsEndpoint, accessToken);
        using var emailsResponse = await httpClientFactory.CreateClient(AuthorizationCodeFlow.HttpClientName).SendAsync(emailsRequest, ct);
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
        TenantSignInFlow flow,
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
            ["redirect_uri"] = flow.CallbackUrl,
            ["code_verifier"] = flow.CodeVerifier,
        };

        if (configuration.ProviderKind == TenantSsoProviderKind.Oidc)
        {
            formValues["grant_type"] = "authorization_code";
        }

        var (statusCode, document) = await AuthorizationCodeFlow.RedeemCodeAsync(
            httpClientFactory.CreateClient(AuthorizationCodeFlow.HttpClientName),
            configuration.TokenEndpoint,
            formValues,
            configuration.ProviderKind == TenantSsoProviderKind.GitHub ? GitHubUserAgent : null,
            logger,
            ct);
        if (document is null)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} StatusCode={StatusCode}",
                provider.TenantId,
                provider.Id,
                "token_exchange_failed",
                (int?)statusCode);
        }

        return document;
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
        identity.IssuerVerified = true;
        identity.LastSignInAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    ///     Links the identity to the account <paramref name="userId" />. A sign-in proved the issuer and subject of the
    ///     identity before this call, so the link is marked as verified. A link of the identity to another account is
    ///     never moved.
    /// </summary>
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
                    IssuerVerified = true,
                    CreatedAt = now,
                    LastSignInAt = now,
                });
        }
        else if (identity.UserId != userId)
        {
            logger.LogWarning(
                "TenantExternalSignInRejected TenantId={TenantId} ProviderId={ProviderId} Reason={Reason} UserId={UserId}",
                tenantId,
                providerId,
                "identity_already_linked",
                userId);
            throw new TenantExternalSignInPolicyException(
                "identity_already_linked",
                "This sign-in is already linked to another account.");
        }
        else
        {
            identity.Email = email;
            identity.EmailVerified = payload.EmailVerified;
            identity.IssuerVerified = true;
            identity.LastSignInAt = now;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    private static string BuildCallbackUrl(Uri applicationBaseUri, string tenantSlug)
    {
        return new Uri(applicationBaseUri, $"auth/external/callback/{Uri.EscapeDataString(tenantSlug)}").ToString();
    }

    private static string BuildAuthorizationUrl(
        TenantSsoProviderRecord provider,
        TenantSsoProviderConfiguration configuration,
        TenantSignInFlow flow)
    {
        var queryParameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", provider.ClientId),
            new("redirect_uri", flow.CallbackUrl),
            new("scope", string.Join(' ', GetEffectiveScopes(provider, configuration.ProviderKind))),
            new("state", flow.State),
            new("code_challenge", AuthorizationCodeFlow.ComputeCodeChallenge(flow.CodeVerifier)),
            new("code_challenge_method", "S256"),
        };

        if (configuration.ProviderKind == TenantSsoProviderKind.Oidc)
        {
            queryParameters.Add(new KeyValuePair<string, string>("response_type", "code"));
            queryParameters.Add(new KeyValuePair<string, string>("nonce", flow.Nonce));
        }

        if (IsEntraProvider(provider))
        {
            queryParameters.Add(new KeyValuePair<string, string>("response_mode", "query"));
        }

        if (configuration.ProviderKind == TenantSsoProviderKind.GitHub)
        {
            queryParameters.Add(new KeyValuePair<string, string>("allow_signup", "false"));
        }

        return AuthorizationCodeFlow.BuildAuthorizationUri(configuration.AuthorizationEndpoint, queryParameters).ToString();
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

        // Multi-tenant Entra authorities (common/organizations/consumers) accept genuinely-signed tokens from any
        // Microsoft tenant, so they are unsupported: each tenant is configured against its own specific authority. Fail
        // closed so a legacy or directly inserted multi-tenant row cannot be used to sign in.
        if (IsMultiTenantEntraAuthority(authority))
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

        // The issuer is the origin of the authority, the host that answers for the user. Two GitHub providers share an
        // issuer only when they reach the same host.
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
        request.Headers.UserAgent.ParseAdd(GitHubUserAgent);
        return request;
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(content);
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
        // A provider can answer with valid JSON that is not an object, such as [] or null. TryGetProperty throws for
        // such a value, so the answer is treated as one without the property.
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
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
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
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

    private static string BuildOidcMetadataAddress(string issuer)
    {
        return $"{issuer.TrimEnd('/')}/.well-known/openid-configuration";
    }

    /// <summary>
    ///     Whether <paramref name="issuer" />, the issuer of a validated ID token, is the authority the provider is
    ///     configured with. Microsoft Entra ID accepts a directory's domain in place of its ID, and its discovery
    ///     document names the ID either way; for an Entra provider at a Microsoft sign-in host, an issuer of a directory
    ///     ID on the same host qualifies for that reason.
    /// </summary>
    private static bool IsIssuerOfAuthority(string issuer, string authority, bool isEntraProvider)
    {
        if (string.Equals(issuer.TrimEnd('/'), authority.TrimEnd('/'), StringComparison.Ordinal))
        {
            return true;
        }

        return isEntraProvider
               && Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
               && Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
               && issuerUri.Scheme == Uri.UriSchemeHttps
               && authorityUri.Scheme == Uri.UriSchemeHttps
               && MicrosoftSignInHosts.Contains(authorityUri.Host, StringComparer.OrdinalIgnoreCase)
               && string.Equals(issuerUri.Host, authorityUri.Host, StringComparison.OrdinalIgnoreCase)
               && issuerUri.IsDefaultPort
               && authorityUri.IsDefaultPort
               && EntraDirectoryIssuerPath.IsMatch(issuerUri.AbsolutePath);
    }

    private static bool IsMultiTenantEntraAuthority(Uri authority)
    {
        // Entra multi-tenant authorities route through the /common, /organizations, or /consumers tenant
        // segment and accept tokens from any Microsoft tenant. Callers reach this only on the Entra branch,
        // so the provider kind is already known.
        var firstSegment = authority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSegment is not null
               && (firstSegment.Equals("common", StringComparison.OrdinalIgnoreCase)
                   || firstSegment.Equals("organizations", StringComparison.OrdinalIgnoreCase)
                   || firstSegment.Equals("consumers", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseBooleanClaim(IEnumerable<Claim> claims, string claimType, out bool value)
    {
        value = false;
        var claimValue = claims.FirstOrDefault(claim => claim.Type == claimType)?.Value;
        if (!bool.TryParse(claimValue, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool IsImplicitlyVerifiedOidcEmail(TenantSsoProviderRecord provider, string? email)
    {
        return IsEntraProvider(provider)
               && !string.IsNullOrWhiteSpace(email)
               && GetEmailDomain(email) is not null;
    }

    private static bool IsEntraProvider(TenantSsoProviderRecord provider)
    {
        return NormalizeProviderKey(provider.ProviderKind) == "entraid";
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

    /// <summary>
    ///     Returns the message the person reads when the provider answers the authorization request with
    ///     <paramref name="error" />, an error code of RFC 6749 or OpenID Connect.
    /// </summary>
    private static string DescribeProviderError(string error)
    {
        return error.Trim() switch
        {
            "access_denied" => "The sign-in was cancelled or refused at the identity provider.",
            "login_required" or "interaction_required" or "consent_required" or "account_selection_required" =>
                "The identity provider needs you to sign in there first. Start the sign-in again.",
            "temporarily_unavailable" or "server_error" => "The identity provider is unavailable. Try again later.",
            _ => "The identity provider refused the sign-in. Ask a tenant administrator to check the provider.",
        };
    }

    /// <summary>A value of the provider for the log, on one line and at most 500 characters long.</summary>
    private static string? ForLog(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var singleLine = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return singleLine.Length <= 500 ? singleLine : singleLine[..500];
    }

    private static TenantExternalSignInCompletionResult Failure(
        string failureCode,
        string message,
        string? frontendReturnUrl = null)
    {
        return new TenantExternalSignInCompletionResult(null, frontendReturnUrl, failureCode, message);
    }

    private static TenantExternalSignInCompletionResult SignedIn(AppUser? user, string? frontendReturnUrl)
    {
        return user is null
            ? Failure("external_identity_rejected", "External identity rejected by tenant policy.", frontendReturnUrl)
            : new TenantExternalSignInCompletionResult(user, frontendReturnUrl);
    }

    private static TenantPendingLinkOutcome PendingLinkNotFound()
    {
        return PendingLinkRefused(
            "pending_link_not_found",
            "No sign-in is waiting to be linked in this browser, or it has expired. Start the sign-in again.");
    }

    private static TenantPendingLinkOutcome PendingLinkRefused(string failureCode, string failureMessage)
    {
        return new TenantPendingLinkOutcome(null, null, failureCode, failureMessage);
    }

    /// <summary>The state of one sign-in, which the browser keeps in a protected cookie until the callback.</summary>
    private sealed record TenantSignInFlow(
        string State,
        string Nonce,
        string CodeVerifier,
        string TenantSlug,
        Guid ProviderId,
        string CallbackUrl,
        string? FrontendReturnUrl,
        TenantExternalSignInMode Mode,
        DateTimeOffset IssuedAtUtc)
    {
        /// <summary>Renders the flow without its state, nonce and code verifier; see <see cref="SecretSafeRendering" />.</summary>
        public override string ToString()
        {
            return $"{nameof(TenantSignInFlow)} {{ TenantSlug = {this.TenantSlug}, ProviderId = {this.ProviderId}, "
                   + $"CodeVerifier = {SecretSafeRendering.Elide(this.CodeVerifier)}, Mode = {this.Mode}, "
                   + $"IssuedAtUtc = {this.IssuedAtUtc:O} }}";
        }
    }

    /// <summary>
    ///     An identity that a provider confirmed and that waits for the confirmation of its link or for a new account.
    ///     The browser keeps it in a protected cookie. <see cref="Id" /> makes each pending link single-use.
    /// </summary>
    internal sealed record TenantPendingLink(
        string Id,
        Guid TenantId,
        Guid ProviderId,
        string Issuer,
        string Subject,
        string Email,
        bool EmailVerified,
        TenantExternalSignInMode Mode,
        DateTimeOffset IssuedAtUtc)
    {
        public TenantExternalIdentityPayload ToPayload()
        {
            return new TenantExternalIdentityPayload(this.Issuer, this.Subject, this.Email, this.EmailVerified, null);
        }
    }

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
