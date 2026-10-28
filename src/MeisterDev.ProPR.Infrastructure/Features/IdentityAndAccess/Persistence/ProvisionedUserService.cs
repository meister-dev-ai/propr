// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Configurations;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>
///     Creates the users of a hosting platform and sets their memberships, as the platform requests through the
///     administration API. ProPR finds a provisioned user by the issuer of the identity provider for provisioned users and
///     the subject of the person, and it changes only accounts that the provisioning created.
/// </summary>
/// <remarks>
///     <para>
///         A provisioned account has no password and no email address of its own. The email address stays on the
///         provisioning record, where no sign-in looks it up.
///     </para>
///     <para>
///         ProPR keeps a provisioned account inactive while it holds no membership. The platform enables it again with a
///         new membership, unless a platform administrator of the installation disabled the account in the meantime.
///     </para>
///     <para>
///         Every change of the memberships of a user holds a lock of that user in PostgreSQL until it commits, so the
///         changes of one user run one after the other, also when the platform sends them at the same time.
///     </para>
/// </remarks>
public sealed class ProvisionedUserService(
    MeisterProPRDbContext dbContext,
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IUserPatRepository userPatRepository,
    IUserAccountAuditLog auditLog,
    IOptions<ProvisionedUserSsoOptions> options) : IProvisionedUserService
{
    private const string NotConfiguredMessage =
        "This installation has no identity provider for provisioned users. Set MEISTER_PROVISIONED_USER_SSO_AUTHORITY "
        + "and the other settings of the provider.";

    /// <inheritdoc />
    public async Task<ProvisioningResult<ProvisionedUserDto>> PutUserAsync(
        string subject,
        string email,
        string? displayName,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        if (options.Value.IdentityIssuer is not { } issuer)
        {
            return new ProvisioningResult<ProvisionedUserDto>(ProvisioningOutcome.ProviderNotConfigured, Message: NotConfiguredMessage);
        }

        var refusal = CheckSubject(subject) ?? CheckEmail(email) ?? CheckDisplayName(displayName);
        if (refusal is not null)
        {
            return new ProvisioningResult<ProvisionedUserDto>(ProvisioningOutcome.InvalidRequest, Message: refusal);
        }

        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        return await this.RetryOnceOnUniqueViolationAsync(() => this.PutUserOnceAsync(issuer, subject, email.Trim(), normalizedDisplayName, actorUserId, ct));
    }

    /// <inheritdoc />
    public async Task<ProvisioningResult<ProvisionedMemberDto>> PutMembershipAsync(
        Guid tenantId,
        string subject,
        IReadOnlyCollection<Guid> clientIds,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientIds);

        var precondition = await this.CheckTenantRequestAsync<ProvisionedMemberDto>(tenantId, subject, ct);
        if (precondition.Refusal is { } refusal)
        {
            return refusal;
        }

        var requested = clientIds.Distinct().ToList();
        var tenantClients = await dbContext.Clients
            .AsNoTracking()
            .Where(client => client.TenantId == tenantId)
            .Select(client => new { client.Id, client.DisplayName })
            .ToListAsync(ct);
        var unknown = requested.Where(clientId => tenantClients.All(client => client.Id != clientId)).ToList();
        if (unknown.Count > 0)
        {
            return new ProvisioningResult<ProvisionedMemberDto>(
                ProvisioningOutcome.ClientNotInTenant,
                Message: "Every client must belong to the tenant.",
                UnknownClientIds: unknown);
        }

        var clientNames = tenantClients.ToDictionary(client => client.Id, client => client.DisplayName);
        return await this.RetryOnceOnUniqueViolationAsync(() => this.PutMembershipOnceAsync(
            tenantId, precondition.Issuer!, subject, requested, clientNames, actorUserId, ct));
    }

    /// <inheritdoc />
    public async Task<ProvisioningResult<bool>> RemoveMembershipAsync(
        Guid tenantId,
        string subject,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var precondition = await this.CheckTenantRequestAsync<bool>(tenantId, subject, ct);
        if (precondition.Refusal is { } refusal)
        {
            return refusal;
        }

        var now = DateTimeOffset.UtcNow;
        bool removed;
        var deactivated = false;
        AppUserRecord account;
        await using (var transaction = await this.BeginTransactionAsync(ct))
        {
            var provisioned = await this.FindAndLockAsync(precondition.Issuer!, subject, ct);
            if (provisioned?.User is not { } user)
            {
                // The platform asks for a state that holds already: the subject has no user, so it has no membership.
                return new ProvisioningResult<bool>(ProvisioningOutcome.Succeeded, false);
            }

            if (user.GlobalRole == AppUserRole.Admin)
            {
                return PlatformAdministrator<bool>();
            }

            account = user;
            var tenantClientIds = dbContext.Clients.Where(client => client.TenantId == tenantId).Select(client => client.Id);
            var membership = await dbContext.TenantMemberships
                .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.UserId == account.Id, ct);
            var assignments = await dbContext.UserClientRoles
                .Where(role => role.UserId == account.Id && tenantClientIds.Contains(role.ClientId))
                .ToListAsync(ct);

            removed = membership is not null || assignments.Count > 0;
            if (membership is not null)
            {
                dbContext.TenantMemberships.Remove(membership);
                this.Audit(
                    tenantId,
                    actorUserId,
                    "tenant.membership.removed",
                    $"Removed tenant membership for {account.Username}.",
                    $"membershipId={membership.Id}; userId={account.Id}; provisionedSubject={subject}",
                    now);
            }

            foreach (var assignment in assignments)
            {
                dbContext.UserClientRoles.Remove(assignment);
                this.Audit(
                    tenantId,
                    actorUserId,
                    "tenant.client-access.removed",
                    $"Revoked client {assignment.ClientId} access for user {account.Id}.",
                    $"userId={account.Id}; clientId={assignment.ClientId}; provisionedSubject={subject}",
                    now);
            }

            var keepsAMembership = await dbContext.TenantMemberships
                .AnyAsync(record => record.UserId == account.Id && record.TenantId != tenantId, ct);
            if (!keepsAMembership && account.IsActive)
            {
                account.IsActive = false;
                provisioned.InactiveWithoutMembershipSince = now;
                deactivated = true;
            }

            await dbContext.SaveChangesAsync(ct);

            // A user without a membership has no access, so no session and no personal access token of the user may
            // stay valid. The revocation runs for every removal that leaves no membership, also when the account was
            // inactive before, so a revocation that failed earlier is finished by the next request of the platform.
            if (!keepsAMembership)
            {
                await refreshTokenRepository.RevokeAllForUserAsync(account.Id, ct);
                await userPatRepository.RevokeAllForUserAsync(account.Id, ct);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }

        if (deactivated)
        {
            auditLog.ProvisionedUserDisabledWithoutMembership(actorUserId ?? Guid.Empty, account.Id, account.Username);
        }

        return new ProvisioningResult<bool>(ProvisioningOutcome.Succeeded, removed);
    }

    /// <inheritdoc />
    public async Task<ProvisioningResult<IReadOnlyList<ProvisionedMemberDto>>> ListMembersAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        if (options.Value.IdentityIssuer is not { } issuer)
        {
            return new ProvisioningResult<IReadOnlyList<ProvisionedMemberDto>>(
                ProvisioningOutcome.ProviderNotConfigured,
                Message: NotConfiguredMessage);
        }

        if (!await dbContext.Tenants.AnyAsync(tenant => tenant.Id == tenantId, ct))
        {
            return new ProvisioningResult<IReadOnlyList<ProvisionedMemberDto>>(ProvisioningOutcome.TenantNotFound);
        }

        var members = await (
                from provisioned in dbContext.ProvisionedUsers.AsNoTracking()
                join membership in dbContext.TenantMemberships.AsNoTracking() on provisioned.UserId equals membership.UserId
                where provisioned.Issuer == issuer && membership.TenantId == tenantId
                orderby provisioned.Subject
                select new
                {
                    provisioned.Subject,
                    provisioned.UserId,
                    provisioned.Email,
                    provisioned.DisplayName,
                    provisioned.User!.IsActive,
                    membership.Role,
                })
            .ToListAsync(ct);

        var userIds = members.Select(member => member.UserId).ToList();
        var tenantClientIds = dbContext.Clients.Where(client => client.TenantId == tenantId).Select(client => client.Id);
        var assignments = await dbContext.UserClientRoles
            .AsNoTracking()
            .Where(role => userIds.Contains(role.UserId) && tenantClientIds.Contains(role.ClientId))
            .Select(role => new { role.UserId, role.ClientId })
            .ToListAsync(ct);

        // The clients are grouped by user once, so the answer for a tenant with many members takes time in proportion to
        // its members and roles.
        var clientIdsByUser = assignments
            .GroupBy(role => role.UserId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Guid>)group.Select(role => role.ClientId).Order().ToList());
        IReadOnlyList<ProvisionedMemberDto> result = members
            .Select(member => new ProvisionedMemberDto(
                member.Subject,
                member.UserId,
                member.Email,
                member.DisplayName,
                member.IsActive,
                member.Role,
                clientIdsByUser.GetValueOrDefault(member.UserId, [])))
            .ToList();
        return new ProvisioningResult<IReadOnlyList<ProvisionedMemberDto>>(ProvisioningOutcome.Succeeded, result);
    }

    private static string? CheckSubject(string? subject)
    {
        // The subject is an opaque identifier and is compared as the token carries it, so it is stored as sent.
        return string.IsNullOrWhiteSpace(subject)
               || subject.Length > ProvisionedUserEntityTypeConfiguration.MaxSubjectLength
               || subject.Any(char.IsControl)
            ? $"The subject must be at most {ProvisionedUserEntityTypeConfiguration.MaxSubjectLength} characters without control characters."
            : null;
    }

    private static string? CheckEmail(string? email)
    {
        var value = email?.Trim();
        var at = value?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return value is null
               || value.Length > ProvisionedUserEntityTypeConfiguration.MaxEmailLength
               || at <= 0
               || at != value.LastIndexOf('@')
               || at == value.Length - 1
               || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            ? $"The email address must be an address of at most {ProvisionedUserEntityTypeConfiguration.MaxEmailLength} characters."
            : null;
    }

    private static string? CheckDisplayName(string? displayName)
    {
        return displayName is not null
               && (displayName.Trim().Length > ProvisionedUserEntityTypeConfiguration.MaxDisplayNameLength
                   || displayName.Any(char.IsControl))
            ? $"The display name must be at most {ProvisionedUserEntityTypeConfiguration.MaxDisplayNameLength} characters on one line."
            : null;
    }

    private static ProvisioningResult<T> PlatformAdministrator<T>()
    {
        return new ProvisioningResult<T>(
            ProvisioningOutcome.PlatformAdministrator,
            Message: "The account of this subject holds platform administrator rights. Provisioning does not change it.");
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    private async Task<ProvisioningResult<ProvisionedUserDto>> PutUserOnceAsync(
        string issuer,
        string subject,
        string email,
        string? displayName,
        Guid? actorUserId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var provisioned = await this.FindAsync(issuer, subject, ct);
        if (provisioned?.User is { } existing)
        {
            if (existing.GlobalRole == AppUserRole.Admin)
            {
                return PlatformAdministrator<ProvisionedUserDto>();
            }

            if (!string.Equals(provisioned.Email, email, StringComparison.Ordinal)
                || !string.Equals(provisioned.DisplayName, displayName, StringComparison.Ordinal))
            {
                provisioned.Email = email;
                provisioned.DisplayName = displayName;
                provisioned.UpdatedAt = now;
                await dbContext.SaveChangesAsync(ct);
            }

            return new ProvisioningResult<ProvisionedUserDto>(ProvisioningOutcome.Succeeded, ToDto(provisioned, existing));
        }

        // The account has no password and no email address, so neither the local sign-in nor the email address of a
        // tenant sign-in reaches it. It stays inactive until the platform sets its first membership.
        var account = new AppUserRecord
        {
            Id = Guid.NewGuid(),
            Username = await ExternalAccountNames.CreateUniqueUsernameAsync(userRepository, email, ct),
            GlobalRole = AppUserRole.User,
            IsActive = false,
            CreatedAt = now,
        };
        var record = new ProvisionedUserRecord
        {
            UserId = account.Id,
            Issuer = issuer,
            Subject = subject,
            Email = email,
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
            InactiveWithoutMembershipSince = now,
        };
        dbContext.AppUsers.Add(account);
        dbContext.ProvisionedUsers.Add(record);
        await dbContext.SaveChangesAsync(ct);

        auditLog.ProvisionedUserCreated(actorUserId ?? Guid.Empty, account.Id, issuer);
        return new ProvisioningResult<ProvisionedUserDto>(ProvisioningOutcome.Created, ToDto(record, account));
    }

    private async Task<ProvisioningResult<ProvisionedMemberDto>> PutMembershipOnceAsync(
        Guid tenantId,
        string issuer,
        string subject,
        IReadOnlyList<Guid> requested,
        IReadOnlyDictionary<Guid, string> clientNames,
        Guid? actorUserId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var reactivated = false;
        TenantMembershipRecord? membership;
        ProvisionedUserRecord provisioned;
        AppUserRecord account;
        await using (var transaction = await this.BeginTransactionAsync(ct))
        {
            var found = await this.FindAndLockAsync(issuer, subject, ct);
            if (found?.User is not { } user)
            {
                return new ProvisioningResult<ProvisionedMemberDto>(
                    ProvisioningOutcome.UserNotFound,
                    Message: "No user is provisioned for this subject. Create the user first.");
            }

            if (user.GlobalRole == AppUserRole.Admin)
            {
                return PlatformAdministrator<ProvisionedMemberDto>();
            }

            provisioned = found;
            account = user;
            membership = await dbContext.TenantMemberships
                .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.UserId == account.Id, ct);
            if (membership is null)
            {
                membership = new TenantMembershipRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = account.Id,
                    Role = TenantRole.TenantUser,
                    AssignedAt = now,
                    UpdatedAt = now,
                };
                dbContext.TenantMemberships.Add(membership);
                this.Audit(
                    tenantId,
                    actorUserId,
                    "tenant.membership.assigned",
                    $"Assigned tenant role {TenantRole.TenantUser} to {account.Username}.",
                    $"membershipId={membership.Id}; userId={account.Id}; role={TenantRole.TenantUser}; provisionedSubject={subject}",
                    now);
            }
            else if (membership.Role != TenantRole.TenantUser)
            {
                membership.Role = TenantRole.TenantUser;
                membership.UpdatedAt = now;
                this.Audit(
                    tenantId,
                    actorUserId,
                    "tenant.membership.updated",
                    $"Updated tenant role for {account.Username} to {TenantRole.TenantUser}.",
                    $"membershipId={membership.Id}; userId={account.Id}; role={TenantRole.TenantUser}; provisionedSubject={subject}",
                    now);
            }

            var tenantClientIds = clientNames.Keys.ToList();
            var assignments = await dbContext.UserClientRoles
                .Where(role => role.UserId == account.Id && tenantClientIds.Contains(role.ClientId))
                .ToListAsync(ct);
            foreach (var assignment in assignments.Where(assignment => !requested.Contains(assignment.ClientId)))
            {
                dbContext.UserClientRoles.Remove(assignment);
                this.Audit(
                    tenantId,
                    actorUserId,
                    "tenant.client-access.removed",
                    $"Revoked client {assignment.ClientId} access for user {account.Id}.",
                    $"membershipId={membership.Id}; userId={account.Id}; clientId={assignment.ClientId}; provisionedSubject={subject}",
                    now);
            }

            foreach (var clientId in requested)
            {
                var assignment = assignments.FirstOrDefault(existing => existing.ClientId == clientId);
                if (assignment is not null && assignment.Role == ClientRole.ClientUser)
                {
                    continue;
                }

                if (assignment is null)
                {
                    dbContext.UserClientRoles.Add(
                        new UserClientRoleRecord
                        {
                            Id = Guid.NewGuid(),
                            UserId = account.Id,
                            ClientId = clientId,
                            Role = ClientRole.ClientUser,
                            AssignedAt = now,
                        });
                }
                else
                {
                    assignment.Role = ClientRole.ClientUser;
                }

                this.Audit(
                    tenantId,
                    actorUserId,
                    "tenant.client-access.assigned",
                    $"Granted {ClientRole.ClientUser} on client {clientNames[clientId]} to user {account.Id}.",
                    $"membershipId={membership.Id}; userId={account.Id}; clientId={clientId}; role={ClientRole.ClientUser}; provisionedSubject={subject}",
                    now);
            }

            // ProPR enables an account that it disabled for the lack of a membership. An account that a platform
            // administrator disabled stays disabled.
            if (!account.IsActive && provisioned.InactiveWithoutMembershipSince is not null)
            {
                account.IsActive = true;
                provisioned.InactiveWithoutMembershipSince = null;
                reactivated = true;
            }

            await dbContext.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }

        if (reactivated)
        {
            auditLog.ProvisionedUserEnabledByMembership(actorUserId ?? Guid.Empty, account.Id, account.Username);
        }

        return new ProvisioningResult<ProvisionedMemberDto>(
            ProvisioningOutcome.Succeeded,
            new ProvisionedMemberDto(
                provisioned.Subject,
                account.Id,
                provisioned.Email,
                provisioned.DisplayName,
                account.IsActive,
                membership.Role,
                requested.Order().ToList()));
    }

    /// <summary>
    ///     Checks what every request about a membership needs: a configured provider, a subject ProPR accepts, and a
    ///     tenant that exists and is not the System tenant.
    /// </summary>
    private async Task<(string? Issuer, ProvisioningResult<T>? Refusal)> CheckTenantRequestAsync<T>(
        Guid tenantId,
        string subject,
        CancellationToken ct)
    {
        if (options.Value.IdentityIssuer is not { } issuer)
        {
            return (null, new ProvisioningResult<T>(ProvisioningOutcome.ProviderNotConfigured, Message: NotConfiguredMessage));
        }

        if (CheckSubject(subject) is { } refusal)
        {
            return (null, new ProvisioningResult<T>(ProvisioningOutcome.InvalidRequest, Message: refusal));
        }

        if (!await dbContext.Tenants.AnyAsync(tenant => tenant.Id == tenantId, ct))
        {
            return (null, new ProvisioningResult<T>(ProvisioningOutcome.TenantNotFound));
        }

        return TenantCatalog.IsEditable(tenantId)
            ? (issuer, null)
            : (null, new ProvisioningResult<T>(
                ProvisioningOutcome.SystemTenant,
                Message: "The System tenant belongs to the installation and has no provisioned members."));
    }

    private Task<ProvisionedUserRecord?> FindAsync(string issuer, string subject, CancellationToken ct)
    {
        return dbContext.ProvisionedUsers
            .Include(provisioned => provisioned.User)
            .FirstOrDefaultAsync(provisioned => provisioned.Issuer == issuer && provisioned.Subject == subject, ct);
    }

    /// <summary>
    ///     Finds the provisioned user of the subject and locks it until the transaction ends. The lock serializes the
    ///     membership changes of one user: a second change waits for the first one, and then reads the memberships and
    ///     the state of the account that the first one committed. Without the lock, two removals in two tenants could
    ///     each still read the membership that the other one removes, and both would keep the account active.
    /// </summary>
    private async Task<ProvisionedUserRecord?> FindAndLockAsync(string issuer, string subject, CancellationToken ct)
    {
        var userId = await dbContext.ProvisionedUsers
            .AsNoTracking()
            .Where(provisioned => provisioned.Issuer == issuer && provisioned.Subject == subject)
            .Select(provisioned => (Guid?)provisioned.UserId)
            .FirstOrDefaultAsync(ct);
        if (userId is not { } id)
        {
            return null;
        }

        if (dbContext.Database.IsRelational())
        {
            await ProvisionedUserLock.AcquireAsync(dbContext, id, ct);
        }

        // The records are read after the lock, so they carry what a change that held the lock before has committed.
        return await this.FindAsync(issuer, subject, ct);
    }

    /// <summary>
    ///     Begins the transaction of a membership change. It returns <see langword="null" /> when the store has no
    ///     transactions, and when the caller holds a transaction already: the change then joins that transaction, and its
    ///     lock lasts until the caller commits.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct)
    {
        return dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(ct)
            : null;
    }

    /// <summary>
    ///     Runs <paramref name="operation" /> again when two requests for the same subject wrote at the same time and a
    ///     unique index refused the second. The second run reads what the first one wrote.
    /// </summary>
    private async Task<T> RetryOnceOnUniqueViolationAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return await operation();
        }
    }

    private void Audit(Guid tenantId, Guid? actorUserId, string eventType, string summary, string detail, DateTimeOffset now)
    {
        dbContext.TenantAuditEntries.Add(
            new TenantAuditEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ActorUserId = actorUserId,
                EventType = eventType,
                Summary = summary,
                Detail = detail,
                OccurredAt = now,
            });
    }

    private static ProvisionedUserDto ToDto(ProvisionedUserRecord provisioned, AppUserRecord account)
    {
        return new ProvisionedUserDto(
            provisioned.Subject,
            account.Id,
            account.Username,
            provisioned.Email,
            provisioned.DisplayName,
            account.IsActive);
    }
}
