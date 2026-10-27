// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>
///     Signs provisioned users in through the identity provider for provisioned users.
///     <see cref="InstallationOidcSignIn" /> runs the flow and returns the identity at the provider.
/// </summary>
/// <remarks>
///     ProPR finds the account through the provisioning record of the issuer and the subject, and through nothing else.
///     The sign-in creates no account, links no account and never looks an account up by its email address. It refuses
///     an identity without a provisioning record, an account that is inactive or holds no membership, and an account
///     with platform administrator rights, each with the same message.
/// </remarks>
public sealed class ProvisionedUserSignInService(
    MeisterProPRDbContext dbContext,
    IUserRepository userRepository,
    InstallationOidcSignIn signIn,
    IUserAccountAuditLog auditLog,
    IOptions<ProvisionedUserSsoOptions> options,
    ILogger<ProvisionedUserSignInService> logger) : IProvisionedUserSignInService
{
    /// <summary>The message of every refusal that concerns the account of the identity.</summary>
    public const string NoAccessMessage = "This account has no access to this installation.";

    private static readonly InstallationSignInKind Kind = new(
        "provisioned_user",
        "auth/provisioned-user/callback",
        "provisioned-user-sso-sign-in-flow",
        "The identity provider cannot be reached. Try again later.");

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
        if (result.Identity is not { } identity)
        {
            return new InstallationSignInCompletion(null, result.FrontendReturnUrl, result.FailureCode, result.FailureMessage);
        }

        // The flow accepts a token of the configured authority only, so the provisioning records of that authority are
        // the ones that can name the identity.
        var issuer = settings.IdentityIssuer!;
        var provisioned = await dbContext.ProvisionedUsers
            .Include(user => user.User)
            .FirstOrDefaultAsync(user => user.Issuer == issuer && user.Subject == identity.Subject, ct);
        if (provisioned?.User is not { } account)
        {
            return this.Refuse(null, null, identity, "not_provisioned", result.FrontendReturnUrl);
        }

        var refusal = account.GlobalRole == AppUserRole.Admin ? "platform_administrator"
            : !account.IsActive ? "account_inactive"
            : !await dbContext.TenantMemberships.AnyAsync(membership => membership.UserId == account.Id, ct) ? "no_membership"
            : null;
        if (refusal is not null)
        {
            return this.Refuse(account.Id, account.Username, identity, refusal, result.FrontendReturnUrl);
        }

        provisioned.LastSignInAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        auditLog.SignedInThroughProvisionedIdentityProvider(account.Id, account.Username, identity.Issuer, identity.Subject);

        var user = await userRepository.GetByIdAsync(account.Id, ct)
                   ?? throw new InvalidOperationException($"The account {account.Id} was not found after the sign-in read it.");
        return new InstallationSignInCompletion(user, result.FrontendReturnUrl);
    }

    private InstallationSignInCompletion Refuse(
        Guid? userId,
        string? username,
        InstallationOidcIdentity identity,
        string reason,
        string? frontendReturnUrl)
    {
        logger.LogWarning("ProvisionedUserSignInRejected Reason={Reason} UserId={UserId}", reason, userId);
        auditLog.ProvisionedIdentityProviderSignInRefused(userId, username, identity.Issuer, identity.Subject, reason);
        return new InstallationSignInCompletion(null, frontendReturnUrl, "no_access", NoAccessMessage);
    }
}
