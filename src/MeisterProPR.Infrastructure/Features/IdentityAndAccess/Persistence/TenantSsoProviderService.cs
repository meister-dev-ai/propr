// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>EF-backed tenant SSO provider persistence service.</summary>
public sealed class TenantSsoProviderService(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    ILogger<TenantSsoProviderService>? logger = null,
    IHttpContextAccessor? httpContextAccessor = null) : ITenantSsoProviderService
{
    private const string SecretPurpose = "tenant-sso-provider-client-secret";

    public async Task<IReadOnlyList<TenantSsoProviderDto>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var providers = await dbContext.TenantSsoProviders
            .AsNoTracking()
            .Where(provider => provider.TenantId == tenantId)
            .OrderBy(provider => provider.DisplayName)
            .ToListAsync(ct);

        return providers.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<TenantSsoProviderDto>> ListEnabledForTenantSlugAsync(string tenantSlug, CancellationToken ct = default)
    {
        var providers = await dbContext.TenantSsoProviders
            .AsNoTracking()
            .Include(provider => provider.Tenant)
            .Where(provider => provider.IsEnabled && provider.Tenant != null && provider.Tenant.Slug == tenantSlug && provider.Tenant.IsActive)
            .OrderBy(provider => provider.DisplayName)
            .ToListAsync(ct);

        return providers.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<TenantSsoProviderDto?> GetByIdAsync(Guid tenantId, Guid providerId, CancellationToken ct = default)
    {
        var provider = await dbContext.TenantSsoProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.Id == providerId, ct);

        return provider is null ? null : ToDto(provider);
    }

    public async Task<TenantSsoProviderDto> CreateAsync(
        Guid tenantId,
        string displayName,
        string providerKind,
        string protocolKind,
        string? issuerOrAuthorityUrl,
        string clientId,
        string? clientSecret,
        IEnumerable<string>? scopes,
        IEnumerable<string>? allowedEmailDomains,
        bool isEnabled,
        bool autoCreateUsers,
        CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var now = DateTimeOffset.UtcNow;
        var provider = new TenantSsoProviderRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = displayName,
            ProviderKind = providerKind,
            ProtocolKind = protocolKind,
            IssuerOrAuthorityUrl = issuerOrAuthorityUrl,
            ClientId = clientId,
            ClientSecretProtected = this.ProtectSecret(clientSecret),
            Scopes = NormalizeStringArray(scopes),
            AllowedEmailDomains = NormalizeDomains(allowedEmailDomains),
            IsEnabled = isEnabled,
            AutoCreateUsers = autoCreateUsers,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.TenantSsoProviders.Add(provider);
        await dbContext.SaveChangesAsync(ct);

        var safeDisplayName = provider.DisplayName.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var safeProviderKind = provider.ProviderKind.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var safeProtocolKind = provider.ProtocolKind.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        logger?.LogInformation(
            "TenantSSOProviderCreated TenantId={TenantId} ProviderId={ProviderId} DisplayName={DisplayName} ProviderKind={ProviderKind} ProtocolKind={ProtocolKind}",
            provider.TenantId,
            provider.Id,
            safeDisplayName,
            safeProviderKind,
            safeProtocolKind);
        await this.AddAuditEntryAsync(
            provider.TenantId,
            "tenant.provider.created",
            $"Created tenant provider '{provider.DisplayName}'.",
            $"providerId={provider.Id}; providerKind={provider.ProviderKind}; protocolKind={provider.ProtocolKind}; isEnabled={provider.IsEnabled}",
            ct);

        return ToDto(provider);
    }

    public async Task<TenantSsoProviderDto?> UpdateAsync(
        Guid tenantId,
        Guid providerId,
        string displayName,
        string providerKind,
        string protocolKind,
        string? issuerOrAuthorityUrl,
        string clientId,
        string? clientSecret,
        IEnumerable<string>? scopes,
        IEnumerable<string>? allowedEmailDomains,
        bool isEnabled,
        bool autoCreateUsers,
        CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var provider = await dbContext.TenantSsoProviders
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.Id == providerId, ct);

        if (provider is null)
        {
            return null;
        }

        var wasEnabled = provider.IsEnabled;

        provider.DisplayName = displayName;
        provider.ProviderKind = providerKind;
        provider.ProtocolKind = protocolKind;
        provider.IssuerOrAuthorityUrl = issuerOrAuthorityUrl;
        provider.ClientId = clientId;
        provider.Scopes = NormalizeStringArray(scopes);
        provider.AllowedEmailDomains = NormalizeDomains(allowedEmailDomains);
        provider.IsEnabled = isEnabled;
        provider.AutoCreateUsers = autoCreateUsers;
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        if (clientSecret is not null)
        {
            provider.ClientSecretProtected = this.ProtectSecret(clientSecret);
        }

        await dbContext.SaveChangesAsync(ct);

        var safeDisplayName = provider.DisplayName.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var safeProviderKind = provider.ProviderKind.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        var safeProtocolKind = provider.ProtocolKind.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        logger?.LogInformation(
            "TenantSSOProviderUpdated TenantId={TenantId} ProviderId={ProviderId} DisplayName={DisplayName} ProviderKind={ProviderKind} ProtocolKind={ProtocolKind} SecretRotated={SecretRotated}",
            provider.TenantId,
            provider.Id,
            safeDisplayName,
            safeProviderKind,
            safeProtocolKind,
            clientSecret is not null);

        if (!wasEnabled && provider.IsEnabled)
        {
            logger?.LogInformation(
                "TenantSSOProviderEnabled TenantId={TenantId} ProviderId={ProviderId} DisplayName={DisplayName}",
                provider.TenantId,
                provider.Id,
                safeDisplayName);
        }
        else if (wasEnabled && !provider.IsEnabled)
        {
            logger?.LogWarning(
                "TenantSSOProviderDisabled TenantId={TenantId} ProviderId={ProviderId} DisplayName={DisplayName}",
                provider.TenantId,
                provider.Id,
                safeDisplayName);
        }

        await this.AddAuditEntryAsync(
            provider.TenantId,
            "tenant.provider.updated",
            $"Updated tenant provider '{provider.DisplayName}'.",
            $"providerId={provider.Id}; providerKind={provider.ProviderKind}; protocolKind={provider.ProtocolKind}; isEnabled={provider.IsEnabled}; secretRotated={clientSecret is not null}",
            ct);

        return ToDto(provider);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid providerId, CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var provider = await dbContext.TenantSsoProviders
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.Id == providerId, ct);

        if (provider is null)
        {
            return false;
        }

        dbContext.TenantSsoProviders.Remove(provider);
        await dbContext.SaveChangesAsync(ct);

        var safeDisplayName = provider.DisplayName.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        logger?.LogWarning(
            "TenantSSOProviderDeleted TenantId={TenantId} ProviderId={ProviderId} DisplayName={DisplayName}",
            provider.TenantId,
            provider.Id,
            safeDisplayName);
        await this.AddAuditEntryAsync(
            provider.TenantId,
            "tenant.provider.deleted",
            $"Deleted tenant provider '{provider.DisplayName}'.",
            $"providerId={provider.Id}; providerKind={provider.ProviderKind}; protocolKind={provider.ProtocolKind}",
            ct);

        return true;
    }

    private async Task AddAuditEntryAsync(
        Guid tenantId,
        string eventType,
        string summary,
        string? detail,
        CancellationToken ct)
    {
        dbContext.TenantAuditEntries.Add(
            new TenantAuditEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ActorUserId = ResolveActorUserId(httpContextAccessor),
                EventType = eventType,
                Summary = summary,
                Detail = detail,
                OccurredAt = DateTimeOffset.UtcNow,
            });

        await dbContext.SaveChangesAsync(ct);
    }

    private string? ProtectSecret(string? clientSecret)
    {
        return string.IsNullOrWhiteSpace(clientSecret)
            ? null
            : secretProtectionCodec.Protect(clientSecret, SecretPurpose);
    }

    private static string[] NormalizeStringArray(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static string[] NormalizeDomains(IEnumerable<string>? domains)
    {
        return domains?
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static Guid? ResolveActorUserId(IHttpContextAccessor? httpContextAccessor)
    {
        var rawUserId = httpContextAccessor?.HttpContext?.Items["UserId"] as string;
        return Guid.TryParse(rawUserId, out var actorUserId) ? actorUserId : null;
    }

    private static void EnsureTenantIsEditable(Guid tenantId)
    {
        if (!TenantCatalog.IsEditable(tenantId))
        {
            throw new InvalidOperationException("The internal System tenant cannot be modified.");
        }
    }

    private static TenantSsoProviderDto ToDto(TenantSsoProviderRecord provider)
    {
        return new TenantSsoProviderDto(
            provider.Id,
            provider.TenantId,
            provider.DisplayName,
            provider.ProviderKind,
            provider.ProtocolKind,
            provider.IssuerOrAuthorityUrl,
            provider.ClientId,
            !string.IsNullOrWhiteSpace(provider.ClientSecretProtected),
            provider.Scopes,
            provider.AllowedEmailDomains,
            provider.IsEnabled,
            provider.AutoCreateUsers,
            provider.CreatedAt,
            provider.UpdatedAt);
    }
}
