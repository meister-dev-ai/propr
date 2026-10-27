// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Claims;
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
///     Signs platform administrators in through the OpenID Connect identity provider configured for the installation.
///     <see cref="InstallationOidcSignIn" /> runs the flow and returns the identity at the provider.
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
    InstallationOidcSignIn signIn,
    IUserAccountAuditLog auditLog,
    IOptions<PlatformAdminSsoOptions> options,
    ILogger<PlatformAdminSignInService> logger) : IPlatformAdminSignInService
{
    private static readonly InstallationSignInKind Kind = new(
        "platform_admin",
        "auth/platform-admin/callback",
        "platform-admin-sso-sign-in-flow",
        "The identity provider for platform administrators cannot be reached. Sign in with a local account or try again later.");

    /// <inheritdoc />
    public string ProviderUnavailableMessage => Kind.UnavailableMessage;

    /// <inheritdoc />
    public Task<InstallationSignInChallenge?> BuildChallengeAsync(
        Uri applicationBaseUri,
        Uri frontendReturnUrl,
        CancellationToken ct = default)
    {
        return signIn.BuildChallengeAsync(Kind, options.Value, applicationBaseUri, frontendReturnUrl, ct);
    }

    /// <inheritdoc />
    public async Task<InstallationSignInCompletion> CompleteSignInAsync(
        Uri applicationBaseUri,
        InstallationSignInCallback callback,
        string? protectedFlow,
        CancellationToken ct = default)
    {
        var settings = options.Value;
        var result = await signIn.CompleteAsync(Kind, settings, applicationBaseUri, callback, protectedFlow, ct);
        if (result.Identity is not { } validated)
        {
            return new InstallationSignInCompletion(null, result.FrontendReturnUrl, result.FailureCode, result.FailureMessage);
        }

        var identity = ReadIdentity(validated, settings);
        return identity.HoldsRequiredRole
            ? await this.SignInWithRoleAsync(identity, result.FrontendReturnUrl!, ct)
            : await this.RefuseWithoutRoleAsync(identity, result.FrontendReturnUrl!, ct);
    }

    /// <summary>Reads the claims that decide the account and its rights from the validated ID token.</summary>
    private static PlatformIdentity ReadIdentity(InstallationOidcIdentity validated, PlatformAdminSsoOptions settings)
    {
        var principal = validated.Principal;
        var emailVerified = bool.TryParse(principal.FindFirst("email_verified")?.Value, out var verified) && verified;
        var holdsRequiredRole = principal.FindAll(settings.EffectiveRoleClaim)
            .Any(claim => string.Equals(claim.Value, settings.RequiredRole, StringComparison.Ordinal));

        return new PlatformIdentity(
            validated.Issuer,
            validated.Subject,
            FirstClaim(principal, "email"),
            emailVerified,
            FirstClaim(principal, "preferred_username"),
            holdsRequiredRole);
    }

    private static string? FirstClaim(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirst(claimType)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<InstallationSignInCompletion> SignInWithRoleAsync(
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

    private async Task<InstallationSignInCompletion> CreateAccountAsync(
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

    private async Task<InstallationSignInCompletion> RefuseWithoutRoleAsync(
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

    private async Task<InstallationSignInCompletion> SignedInAsync(Guid userId, string frontendReturnUrl, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(userId, ct)
                   ?? throw new InvalidOperationException($"The account {userId} was not found after the sign-in saved it.");
        return new InstallationSignInCompletion(user, frontendReturnUrl);
    }

    private InstallationSignInCompletion Refuse(string failureCode, string failureMessage, string? frontendReturnUrl)
    {
        logger.LogWarning("PlatformAdminSignInRejected Reason={Reason}", failureCode);
        return new InstallationSignInCompletion(null, frontendReturnUrl, failureCode, failureMessage);
    }

    /// <summary>The identity at the provider, as the validated ID token states it.</summary>
    private sealed record PlatformIdentity(
        string Issuer,
        string Subject,
        string? Email,
        bool EmailVerified,
        string? PreferredUsername,
        bool HoldsRequiredRole);
}
