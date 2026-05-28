// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for client-scoped SCM provider connections.</summary>
public sealed class ClientScmConnectionRepository(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IProviderActivationService? providerActivationService = null,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IClientScmConnectionRepository
{
    private const string SecretPurpose = "ClientScmConnectionSecret";

    public async Task<IReadOnlyList<ClientScmConnectionDto>> GetByClientIdAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        var records = await this.WithReadDbAsync(
            db => db.ClientScmConnections
                .AsNoTracking()
                .Where(connection => connection.ClientId == clientId)
                .OrderBy(connection => connection.DisplayName)
                .ToListAsync(ct),
            ct);

        if (providerActivationService is not null)
        {
            var enabledProviders = await providerActivationService.GetEnabledProvidersAsync(ct);
            return records
                .Where(record => enabledProviders.Contains(record.Provider))
                .Select(ToDto)
                .ToList()
                .AsReadOnly();
        }

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<ClientScmConnectionDto?> GetByIdAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        var record = await this.WithReadDbAsync(
            db => db.ClientScmConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(connection => connection.ClientId == clientId && connection.Id == connectionId, ct),
            ct);

        if (record is not null && providerActivationService is not null)
        {
            var enabled = await providerActivationService.IsEnabledAsync(record.Provider, ct);
            if (!enabled)
            {
                return null;
            }
        }

        return record is null ? null : ToDto(record);
    }

    public async Task<ClientScmConnectionCredentialDto?> GetOperationalConnectionAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (providerActivationService is not null && !await providerActivationService.IsEnabledAsync(host.Provider, ct))
        {
            return null;
        }

        ClientScmConnectionRecord? record;
        if (host.Provider == ScmProvider.AzureDevOps)
        {
            record = (await this.WithReadDbAsync(
                    db => db.ClientScmConnections
                        .AsNoTracking()
                        .Where(connection =>
                            connection.ClientId == clientId
                            && connection.Provider == host.Provider
                            && connection.IsActive)
                        .ToListAsync(ct),
                    ct))
                .Where(connection => AzureDevOpsHostBaseUrlMatches(connection.HostBaseUrl, host.HostBaseUrl))
                .OrderByDescending(connection => connection.HostBaseUrl.Length)
                .FirstOrDefault();
        }
        else
        {
            record = await this.WithReadDbAsync(
                db => db.ClientScmConnections
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        connection =>
                            connection.ClientId == clientId
                            && connection.Provider == host.Provider
                            && connection.HostBaseUrl == host.HostBaseUrl
                            && connection.IsActive,
                        ct),
                ct);
        }

        return record is null ? null : this.ToCredentialDto(record);
    }

    public async Task<ClientScmConnectionCredentialDto?> GetOperationalConnectionByIdAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        var record = await this.WithReadDbAsync(
            db => db.ClientScmConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    connection =>
                        connection.ClientId == clientId
                        && connection.Id == connectionId
                        && connection.IsActive,
                    ct),
            ct);

        if (record is null)
        {
            return null;
        }

        if (providerActivationService is not null && !await providerActivationService.IsEnabledAsync(record.Provider, ct))
        {
            return null;
        }

        return this.ToCredentialDto(record);
    }

    public async Task<ClientScmConnectionDto?> AddAsync(
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId,
        string displayName,
        string secret,
        bool isActive,
        long? gitHubAppId = null,
        long? gitHubAppInstallationId = null,
        string? userName = null,
        CancellationToken ct = default)
    {
        if (!await dbContext.Clients.AnyAsync(client => client.Id == clientId, ct))
        {
            return null;
        }

        var normalizedHostBaseUrl = NormalizeHostBaseUrl(providerFamily, hostBaseUrl);
        if (await dbContext.ClientScmConnections.AnyAsync(
                connection => connection.ClientId == clientId
                              && connection.Provider == providerFamily
                              && connection.HostBaseUrl == normalizedHostBaseUrl,
                ct))
        {
            throw new InvalidOperationException("A provider connection for this provider family and host already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var usesGitHubAppInstallation = UsesGitHubAppInstallation(providerFamily, authenticationKind);
        var record = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Provider = providerFamily,
            HostBaseUrl = normalizedHostBaseUrl,
            AuthenticationKind = authenticationKind,
            UserName = NormalizeUserName(providerFamily, authenticationKind, userName),
            OAuthTenantId = NormalizeOptional(oAuthTenantId),
            OAuthClientId = NormalizeOptional(oAuthClientId),
            GitHubAppId = usesGitHubAppInstallation
                ? NormalizeRequiredPositiveIdentifier(gitHubAppId, nameof(gitHubAppId))
                : null,
            GitHubAppInstallationId = usesGitHubAppInstallation
                ? NormalizeRequiredPositiveIdentifier(gitHubAppInstallationId, nameof(gitHubAppInstallationId))
                : null,
            DisplayName = NormalizeRequired(displayName),
            EncryptedSecretMaterial = secretProtectionCodec.Protect(NormalizeRequired(secret), SecretPurpose),
            VerificationStatus = "unknown",
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await this.PurgeExpiredAuditEntriesAsync(ct);
        dbContext.ClientScmConnections.Add(record);
        dbContext.ProviderConnectionAuditEntries.Add(
            CreateAuditEntry(
                record,
                "connectionCreated",
                $"Connection created for {record.DisplayName}.",
                "info",
                occurredAt: now));
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<ClientScmConnectionDto?> UpdateAsync(
        Guid clientId,
        Guid connectionId,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId,
        string displayName,
        string? secret,
        bool isActive,
        long? gitHubAppId = null,
        long? gitHubAppInstallationId = null,
        string? userName = null,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmConnections
            .FirstOrDefaultAsync(connection => connection.ClientId == clientId && connection.Id == connectionId, ct);

        if (record is null)
        {
            return null;
        }

        var previousHostBaseUrl = record.HostBaseUrl;
        var normalizedHostBaseUrl = NormalizeHostBaseUrl(record.Provider, hostBaseUrl);
        if (await dbContext.ClientScmConnections.AnyAsync(
                connection => connection.ClientId == clientId
                              && connection.Id != connectionId
                              && connection.Provider == record.Provider
                              && connection.HostBaseUrl == normalizedHostBaseUrl,
                ct))
        {
            throw new InvalidOperationException("A provider connection for this provider family and host already exists.");
        }

        var onboardingInputsChanged = !string.Equals(
                                          record.HostBaseUrl,
                                          normalizedHostBaseUrl,
                                          StringComparison.OrdinalIgnoreCase)
                                      || record.AuthenticationKind != authenticationKind
                                      || !string.Equals(
                                          record.UserName,
                                          NormalizeUserName(record.Provider, authenticationKind, userName),
                                          StringComparison.Ordinal)
                                      || !string.Equals(
                                          record.OAuthTenantId,
                                          NormalizeOptional(oAuthTenantId),
                                          StringComparison.Ordinal)
                                      || !string.Equals(
                                          record.OAuthClientId,
                                          NormalizeOptional(oAuthClientId),
                                          StringComparison.Ordinal)
                                      || record.GitHubAppId != NormalizeGitHubAppIdentifier(
                                          record.Provider,
                                          authenticationKind,
                                          gitHubAppId,
                                          nameof(gitHubAppId))
                                      || record.GitHubAppInstallationId != NormalizeGitHubAppIdentifier(
                                          record.Provider,
                                          authenticationKind,
                                          gitHubAppInstallationId,
                                          nameof(gitHubAppInstallationId))
                                      || !string.IsNullOrWhiteSpace(secret);
        var wasActive = record.IsActive;
        var secretRotated = !string.IsNullOrWhiteSpace(secret);

        record.HostBaseUrl = normalizedHostBaseUrl;
        record.AuthenticationKind = authenticationKind;
        record.UserName = NormalizeUserName(record.Provider, authenticationKind, userName);
        record.OAuthTenantId = NormalizeOptional(oAuthTenantId);
        record.OAuthClientId = NormalizeOptional(oAuthClientId);
        record.GitHubAppId = NormalizeGitHubAppIdentifier(
            record.Provider,
            authenticationKind,
            gitHubAppId,
            nameof(gitHubAppId));
        record.GitHubAppInstallationId = NormalizeGitHubAppIdentifier(
            record.Provider,
            authenticationKind,
            gitHubAppInstallationId,
            nameof(gitHubAppInstallationId));
        record.DisplayName = NormalizeRequired(displayName);
        record.IsActive = isActive;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(secret))
        {
            record.EncryptedSecretMaterial = secretProtectionCodec.Protect(NormalizeRequired(secret), SecretPurpose);
        }

        if (record.Provider == ScmProvider.AzureDevOps
            && !string.Equals(previousHostBaseUrl, normalizedHostBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            this.RepointAzureDevOpsOrganizationScopes(clientId, connectionId, previousHostBaseUrl, normalizedHostBaseUrl);
        }

        if (onboardingInputsChanged)
        {
            record.VerificationStatus = "unknown";
            record.LastVerifiedAt = null;
            record.LastVerificationError = null;
            record.LastVerificationFailureCategory = null;
        }

        await this.PurgeExpiredAuditEntriesAsync(ct);
        dbContext.ProviderConnectionAuditEntries.Add(
            CreateAuditEntry(
                record,
                ResolveMutationEventType(wasActive, record.IsActive, secretRotated),
                BuildMutationSummary(record.DisplayName, wasActive, record.IsActive, secretRotated),
                wasActive && !record.IsActive ? "warning" : "info",
                onboardingInputsChanged
                    ? "Connection settings changed and verification was reset."
                    : null,
                occurredAt: record.UpdatedAt));
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<ClientScmConnectionDto?> UpdateVerificationAsync(
        Guid clientId,
        Guid connectionId,
        string verificationStatus,
        DateTimeOffset verifiedAt,
        string? verificationError,
        CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmConnections
            .FirstOrDefaultAsync(connection => connection.ClientId == clientId && connection.Id == connectionId, ct);

        if (record is null)
        {
            return null;
        }

        record.VerificationStatus = NormalizeRequired(verificationStatus);
        record.LastVerifiedAt = verifiedAt;
        record.LastVerificationError = NormalizeOptional(verificationError);
        record.LastVerificationFailureCategory =
            ProviderRetentionPolicy.CategorizeFailure(verificationError, "discovery");
        record.UpdatedAt = DateTimeOffset.UtcNow;

        await this.PurgeExpiredAuditEntriesAsync(ct);
        dbContext.ProviderConnectionAuditEntries.Add(
            CreateAuditEntry(
                record,
                ResolveVerificationEventType(record.VerificationStatus),
                BuildVerificationSummary(record.DisplayName, record.VerificationStatus),
                ResolveVerificationAuditStatus(record.VerificationStatus),
                record.LastVerificationError,
                record.LastVerificationFailureCategory,
                verifiedAt));
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<bool> DeleteAsync(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.ClientScmConnections
            .FirstOrDefaultAsync(connection => connection.ClientId == clientId && connection.Id == connectionId, ct);

        if (record is null)
        {
            return false;
        }

        await this.PurgeExpiredAuditEntriesAsync(ct);
        dbContext.ProviderConnectionAuditEntries.Add(
            CreateAuditEntry(
                record,
                "connectionDeleted",
                $"Connection deleted for {record.DisplayName}.",
                "warning",
                occurredAt: DateTimeOffset.UtcNow));
        dbContext.ClientScmConnections.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    ///     For Azure DevOps provider connections, if the host URL changes, we need to update all existing scopes that point to the old host URL so they continue to
    ///     function correctly.
    ///     Otherwise, the scopes would still point at the old host and fail discovery and verification until manually updated.
    ///     This method performs that repointing automatically during connection updates.
    /// </summary>
    private void RepointAzureDevOpsOrganizationScopes(
        Guid clientId,
        Guid connectionId,
        string previousHostBaseUrl,
        string newHostBaseUrl)
    {
        foreach (var scope in dbContext.ClientScmScopes.Where(scope => scope.ClientId == clientId && scope.ConnectionId == connectionId))
        {
            if (!TryRepointAzureDevOpsScopePath(scope.ScopePath, previousHostBaseUrl, newHostBaseUrl, out var repointedScopePath))
            {
                continue;
            }

            scope.ScopePath = repointedScopePath;
            scope.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<T> WithReadDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var readDb = await contextFactory.CreateDbContextAsync(ct);
        return await operation(readDb);
    }

    private static ClientScmConnectionDto ToDto(ClientScmConnectionRecord record)
    {
        return new ClientScmConnectionDto(
            record.Id,
            record.ClientId,
            record.Provider,
            record.HostBaseUrl,
            record.AuthenticationKind,
            record.OAuthTenantId,
            record.OAuthClientId,
            record.DisplayName,
            record.IsActive,
            record.VerificationStatus,
            record.LastVerifiedAt,
            record.LastVerificationError,
            record.LastVerificationFailureCategory,
            record.CreatedAt,
            record.UpdatedAt,
            GitHubAppId: record.GitHubAppId,
            GitHubAppInstallationId: record.GitHubAppInstallationId,
            UserName: record.UserName);
    }

    private ClientScmConnectionCredentialDto ToCredentialDto(ClientScmConnectionRecord record)
    {
        return new ClientScmConnectionCredentialDto(
            record.Id,
            record.ClientId,
            record.Provider,
            record.HostBaseUrl,
            record.AuthenticationKind,
            record.OAuthTenantId,
            record.OAuthClientId,
            record.DisplayName,
            secretProtectionCodec.Unprotect(record.EncryptedSecretMaterial, SecretPurpose),
            record.IsActive,
            record.GitHubAppId,
            record.GitHubAppInstallationId,
            record.UserName);
    }

    public async Task<ClientScmConnectionDto?> AddAsync(
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string displayName,
        string secret,
        bool isActive,
        CancellationToken ct = default)
    {
        return await this.AddAsync(
            clientId,
            providerFamily,
            hostBaseUrl,
            authenticationKind,
            null,
            null,
            displayName,
            secret,
            isActive,
            null,
            null,
            null,
            ct);
    }

    public async Task<ClientScmConnectionDto?> UpdateAsync(
        Guid clientId,
        Guid connectionId,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string displayName,
        string? secret,
        bool isActive,
        CancellationToken ct = default)
    {
        return await this.UpdateAsync(
            clientId,
            connectionId,
            hostBaseUrl,
            authenticationKind,
            null,
            null,
            displayName,
            secret,
            isActive,
            null,
            null,
            null,
            ct);
    }

    private static bool UsesGitHubAppInstallation(ScmProvider providerFamily, ScmAuthenticationKind authenticationKind)
    {
        return providerFamily == ScmProvider.GitHub
               && authenticationKind == ScmAuthenticationKind.AppInstallation;
    }

    private static long? NormalizeGitHubAppIdentifier(
        ScmProvider providerFamily,
        ScmAuthenticationKind authenticationKind,
        long? value,
        string parameterName)
    {
        return UsesGitHubAppInstallation(providerFamily, authenticationKind)
            ? NormalizeRequiredPositiveIdentifier(value, parameterName)
            : null;
    }

    private static long NormalizeRequiredPositiveIdentifier(long? value, string parameterName)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            throw new InvalidOperationException($"{parameterName} must be a positive numeric identifier.");
        }

        return value.Value;
    }

    private static string? NormalizeUserName(
        ScmProvider providerFamily,
        ScmAuthenticationKind authenticationKind,
        string? userName)
    {
        if (providerFamily != ScmProvider.AzureDevOps || authenticationKind != ScmAuthenticationKind.WindowsUserAccount)
        {
            return null;
        }

        return NormalizeRequired(userName, nameof(userName), 256);
    }

    private async Task PurgeExpiredAuditEntriesAsync(CancellationToken ct)
    {
        var cutoff = ProviderRetentionPolicy.GetProviderConnectionAuditCutoff(DateTimeOffset.UtcNow);
        var expiredEntries = await dbContext.ProviderConnectionAuditEntries
            .Where(entry => entry.OccurredAt < cutoff)
            .ToListAsync(ct);

        if (expiredEntries.Count == 0)
        {
            return;
        }

        dbContext.ProviderConnectionAuditEntries.RemoveRange(expiredEntries);
        await dbContext.SaveChangesAsync(ct);
    }

    private static ProviderConnectionAuditEntryRecord CreateAuditEntry(
        ClientScmConnectionRecord record,
        string eventType,
        string summary,
        string status,
        string? detail = null,
        string? failureCategory = null,
        DateTimeOffset? occurredAt = null)
    {
        return new ProviderConnectionAuditEntryRecord
        {
            Id = Guid.NewGuid(),
            ClientId = record.ClientId,
            ConnectionId = record.Id,
            Provider = record.Provider,
            HostBaseUrl = record.HostBaseUrl,
            DisplayName = record.DisplayName,
            EventType = NormalizeOptional(eventType) ?? "connectionUpdated",
            Summary = NormalizeAuditText(summary, 256) ?? "Provider connection updated.",
            Status = ProviderRetentionPolicy.NormalizeStatus(status, "info"),
            FailureCategory = NormalizeOptional(failureCategory),
            Detail = NormalizeAuditText(detail, 2048),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        };
    }

    private static string ResolveMutationEventType(bool wasActive, bool isActive, bool secretRotated)
    {
        if (secretRotated)
        {
            return "connectionRotated";
        }

        if (wasActive && !isActive)
        {
            return "connectionDisabled";
        }

        if (!wasActive && isActive)
        {
            return "connectionEnabled";
        }

        return "connectionUpdated";
    }

    private static string BuildMutationSummary(string displayName, bool wasActive, bool isActive, bool secretRotated)
    {
        if (secretRotated)
        {
            return $"Connection credentials rotated for {displayName}.";
        }

        if (wasActive && !isActive)
        {
            return $"Connection disabled for {displayName}.";
        }

        if (!wasActive && isActive)
        {
            return $"Connection enabled for {displayName}.";
        }

        return $"Connection updated for {displayName}.";
    }

    private static string ResolveVerificationEventType(string verificationStatus)
    {
        return verificationStatus.Trim().ToLowerInvariant() switch
        {
            "verified" => "connectionVerified",
            "failed" => "connectionVerificationFailed",
            _ => "connectionVerificationStale",
        };
    }

    private static string BuildVerificationSummary(string displayName, string verificationStatus)
    {
        return verificationStatus.Trim().ToLowerInvariant() switch
        {
            "verified" => $"Connection verified for {displayName}.",
            "failed" => $"Verification failed for {displayName}.",
            _ => $"Connection needs re-verification for {displayName}.",
        };
    }

    private static string ResolveVerificationAuditStatus(string verificationStatus)
    {
        return verificationStatus.Trim().ToLowerInvariant() switch
        {
            "verified" => "success",
            "failed" => "error",
            _ => "warning",
        };
    }

    private static string? NormalizeAuditText(string? value, int maxLength)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd();
    }

    private static string NormalizeHostBaseUrl(ScmProvider providerFamily, string hostBaseUrl)
    {
        if (providerFamily == ScmProvider.AzureDevOps && !IsHostedAzureDevOpsHost(hostBaseUrl))
        {
            return NormalizeAbsoluteUrlPreservingPath(hostBaseUrl);
        }

        return new ProviderHostRef(providerFamily, hostBaseUrl).HostBaseUrl;
    }

    private static bool AzureDevOpsHostBaseUrlMatches(string left, string right)
    {
        var normalizedLeft = left.Trim().TrimEnd('/');
        var normalizedRight = right.Trim().TrimEnd('/');
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedLeft.StartsWith(normalizedRight + "/", StringComparison.OrdinalIgnoreCase)
               || normalizedRight.StartsWith(normalizedLeft + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRepointAzureDevOpsScopePath(
        string scopePath,
        string previousHostBaseUrl,
        string newHostBaseUrl,
        out string repointedScopePath)
    {
        repointedScopePath = string.Empty;
        if (!Uri.TryCreate(scopePath.Trim(), UriKind.Absolute, out _))
        {
            return false;
        }

        var normalizedScopePath = NormalizeAbsoluteUrlPreservingPath(scopePath);
        var normalizedPreviousHostBaseUrl = NormalizeAbsoluteUrlPreservingPath(previousHostBaseUrl);
        var normalizedNewHostBaseUrl = NormalizeAbsoluteUrlPreservingPath(newHostBaseUrl);
        if (!string.Equals(normalizedScopePath, normalizedPreviousHostBaseUrl, StringComparison.OrdinalIgnoreCase)
            && !normalizedScopePath.StartsWith(normalizedPreviousHostBaseUrl + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = normalizedScopePath[normalizedPreviousHostBaseUrl.Length..];
        repointedScopePath = (normalizedNewHostBaseUrl + suffix).TrimEnd('/');
        return true;
    }

    private static string NormalizeAbsoluteUrlPreservingPath(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("HostBaseUrl must be an absolute URL.", nameof(value));
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static bool IsHostedAzureDevOpsHost(string hostBaseUrl)
    {
        if (!Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new InvalidOperationException($"{parameterName} must not exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
