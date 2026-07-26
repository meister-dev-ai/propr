// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for provider-neutral AI connection profiles.</summary>
public sealed class AiConnectionRepository(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
    ITenantProviderPolicyProvider? providerPolicies = null,
    IAiProviderConfigAuditWriter? configAudit = null) : IAiConnectionRepository
{
    private const string SecretPurpose = "AiConnectionApiKey";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiConnectionDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var records = await this.WithReadDbAsync(
            db => db.AiConnectionProfiles
                .Include(profile => profile.ConfiguredModels)
                .Include(profile => profile.PurposeBindings)
                .Include(profile => profile.VerificationSnapshot)
                .Where(profile => profile.ClientId == clientId)
                .OrderByDescending(profile => profile.CreatedAt)
                .AsNoTracking()
                .ToListAsync(ct),
            ct);

        return records
            .Select(this.ToDto)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiConnectionDto>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var records = await this.WithReadDbAsync(
            db => db.AiConnectionProfiles
                .Include(profile => profile.ConfiguredModels)
                .Include(profile => profile.PurposeBindings)
                .Include(profile => profile.VerificationSnapshot)
                .Where(profile => profile.TenantId == tenantId)
                .OrderByDescending(profile => profile.CreatedAt)
                .AsNoTracking()
                .ToListAsync(ct),
            ct);

        return records
            .Select(this.ToDto)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Uses a short-lived <see cref="MeisterProPRDbContext" /> from the factory so concurrent
    ///     calls from parallel file-review tasks cannot share the same context instance.
    /// </remarks>
    public async Task<AiConnectionDto?> GetActiveForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var record = await this.WithReadDbAsync(
            db => db.AiConnectionProfiles
                .Include(profile => profile.ConfiguredModels)
                .Include(profile => profile.PurposeBindings)
                .Include(profile => profile.VerificationSnapshot)
                .Where(profile => profile.ClientId == clientId && profile.IsActive)
                .OrderBy(profile => profile.DisplayName)
                .ThenBy(profile => profile.Id)
                .FirstOrDefaultAsync(ct),
            ct);
        return record is null ? null : this.ToDto(record);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await this.WithReadDbAsync(
            db => db.AiConnectionProfiles
                .Include(profile => profile.ConfiguredModels)
                .Include(profile => profile.PurposeBindings)
                .Include(profile => profile.VerificationSnapshot)
                .FirstOrDefaultAsync(profile => profile.Id == connectionId, ct),
            ct);
        return record is null ? null : this.ToDto(record);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto> AddAsync(
        Guid clientId,
        AiConnectionWriteRequestDto request,
        CancellationToken ct = default)
    {
        await this.GuardProviderPolicyForClientAsync(clientId, request.ProviderKind, request.BaseUrl, ct);

        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuredModels = BuildConfiguredModels(profileId, request, null);
        var bindings = BuildPurposeBindings(profileId, request.PurposeBindings, configuredModels, now);

        var record = new AiConnectionProfileRecord
        {
            Id = profileId,
            ClientId = clientId,
            DisplayName = request.DisplayName,
            ProviderKind = request.ProviderKind.ToString(),
            BaseUrl = request.BaseUrl,
            AuthMode = request.AuthMode.ToString(),
            ProtectedSecret = this.ProtectSecret(request.Secret, request.AuthMode),
            DefaultHeaders = NormalizeMap(request.DefaultHeaders),
            DefaultQueryParams = NormalizeMap(request.DefaultQueryParams),
            DiscoveryMode = request.DiscoveryMode.ToString(),
            IsActive = false,
            ConfiguredModels = configuredModels,
            PurposeBindings = bindings,
            VerificationSnapshot = ToVerificationRecord(profileId, AiVerificationResultDto.NeverVerified),
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.AiConnectionProfiles.Add(record);
        await dbContext.SaveChangesAsync(ct);
        await this.AuditAsync("created", record, request.Secret is not null, ct);
        return this.ToDto(record);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto> AddTenantAsync(Guid tenantId, AiConnectionWriteRequestDto request, CancellationToken ct = default)
    {
        await this.GuardProviderPolicyForTenantAsync(tenantId, request.ProviderKind, request.BaseUrl, ct);

        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuredModels = BuildConfiguredModels(profileId, request, null);
        var bindings = BuildPurposeBindings(profileId, request.PurposeBindings, configuredModels, now);

        var record = new AiConnectionProfileRecord
        {
            Id = profileId,
            ClientId = null,
            TenantId = tenantId,
            DisplayName = request.DisplayName,
            ProviderKind = request.ProviderKind.ToString(),
            BaseUrl = request.BaseUrl,
            AuthMode = request.AuthMode.ToString(),
            ProtectedSecret = this.ProtectSecret(request.Secret, request.AuthMode),
            DefaultHeaders = NormalizeMap(request.DefaultHeaders),
            DefaultQueryParams = NormalizeMap(request.DefaultQueryParams),
            DiscoveryMode = request.DiscoveryMode.ToString(),
            IsActive = false,
            ConfiguredModels = configuredModels,
            PurposeBindings = bindings,
            VerificationSnapshot = ToVerificationRecord(profileId, AiVerificationResultDto.NeverVerified),
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.AiConnectionProfiles.Add(record);
        await dbContext.SaveChangesAsync(ct);
        await this.AuditAsync("created", record, request.Secret is not null, ct);
        return this.ToDto(record);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid connectionId,
        AiConnectionWriteRequestDto request,
        CancellationToken ct = default)
    {
        var record = await dbContext.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .Include(profile => profile.PurposeBindings)
            .Include(profile => profile.VerificationSnapshot)
            .FirstOrDefaultAsync(profile => profile.Id == connectionId, ct);
        if (record is null)
        {
            return false;
        }

        // An update can change the provider family, so the policy is checked here too rather than only on create.
        if (record.TenantId is { } ownerTenantId)
        {
            await this.GuardProviderPolicyForTenantAsync(ownerTenantId, request.ProviderKind, request.BaseUrl, ct);
        }
        else if (record.ClientId is { } ownerClientId)
        {
            await this.GuardProviderPolicyForClientAsync(ownerClientId, request.ProviderKind, request.BaseUrl, ct);
        }

        var updatedModels = BuildConfiguredModels(record.Id, request, record.ConfiguredModels);
        var now = DateTimeOffset.UtcNow;
        var updatedBindings = BuildPurposeBindings(record.Id, request.PurposeBindings, updatedModels, now);
        var shouldInvalidateVerification = this.RequiresVerificationReset(record, request, updatedModels, updatedBindings);

        // Block dropping a configured model that a logical model still maps to. Models kept across the update retain
        // their id (BuildConfiguredModels matches by remote model id), so "removed" = old ids no longer present.
        var removedModelIds = record.ConfiguredModels
            .Select(model => model.Id)
            .Except(updatedModels.Select(model => model.Id))
            .ToList();
        if (removedModelIds.Count > 0)
        {
            await this.GuardLogicalModelReferencesAsync([], removedModelIds, ct);
        }

        record.DisplayName = request.DisplayName;
        record.ProviderKind = request.ProviderKind.ToString();
        record.BaseUrl = request.BaseUrl;
        record.AuthMode = request.AuthMode.ToString();
        record.DiscoveryMode = request.DiscoveryMode.ToString();
        record.DefaultHeaders = NormalizeMap(request.DefaultHeaders);
        record.DefaultQueryParams = NormalizeMap(request.DefaultQueryParams);
        record.ProtectedSecret = request.Secret is null
            ? record.ProtectedSecret
            : this.ProtectSecret(request.Secret, request.AuthMode);
        record.UpdatedAt = now;

        record.ConfiguredModels.Clear();
        foreach (var model in updatedModels)
        {
            record.ConfiguredModels.Add(model);
        }

        record.PurposeBindings.Clear();
        foreach (var binding in updatedBindings)
        {
            record.PurposeBindings.Add(binding);
        }

        if (shouldInvalidateVerification)
        {
            record.VerificationSnapshot = ToVerificationRecord(connectionId, AiVerificationResultDto.NeverVerified);
        }

        await dbContext.SaveChangesAsync(ct);
        await this.AuditAsync("updated", record, request.Secret is not null, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .FirstOrDefaultAsync(profile => profile.Id == connectionId, ct);
        if (record is null)
        {
            return false;
        }

        // A logical model must not be silently orphaned: block deleting a connection any logical model maps to,
        // whether by the connection id or one of its configured models. The referrers are named in the error.
        var modelIds = record.ConfiguredModels.Select(model => model.Id).ToList();
        await this.GuardLogicalModelReferencesAsync([connectionId], modelIds, ct);

        dbContext.AiConnectionProfiles.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        await this.AuditAsync("deleted", record, false, ct);
        return true;
    }

    /// <summary>
    ///     Throws <see cref="LogicalModelReferenceInUseException" /> when any logical model (tenant catalog or per-client
    ///     override) maps to one of the given connection ids or configured-model ids. Queried directly against the
    ///     logical-model tables (not the catalog repository) to avoid a dependency cycle.
    /// </summary>
    private async Task GuardLogicalModelReferencesAsync(
        IReadOnlyCollection<Guid> connectionIds,
        IReadOnlyCollection<Guid> configuredModelIds,
        CancellationToken ct)
    {
        var tenantRefs = await dbContext.LogicalModels
            .Where(x => connectionIds.Contains(x.ConnectionId) || configuredModelIds.Contains(x.ConfiguredModelId))
            .Select(x => x.Name)
            .ToListAsync(ct);
        var clientRefs = await dbContext.LogicalModelOverrides
            .Where(x => connectionIds.Contains(x.ConnectionId) || configuredModelIds.Contains(x.ConfiguredModelId))
            .Select(x => x.Name)
            .ToListAsync(ct);

        var referrers = tenantRefs.Concat(clientRefs).Distinct(StringComparer.Ordinal).ToList();
        if (referrers.Count > 0)
        {
            throw new LogicalModelReferenceInUseException(referrers);
        }
    }

    /// <inheritdoc />
    public async Task<AiConnectionActivationResultDto> ActivateAsync(Guid connectionId, CancellationToken ct = default)
    {
        var target = await dbContext.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .Include(profile => profile.PurposeBindings)
            .Include(profile => profile.VerificationSnapshot)
            .FirstOrDefaultAsync(profile => profile.Id == connectionId, ct);
        if (target is null)
        {
            return AiConnectionActivationResultDto.NotFound;
        }

        // Active means "in use", not "the one". Several profiles can be active at once so a client can mix
        // providers — which model serves which role is decided by logical models, not by which profile won a
        // race for a single slot. Activating one therefore leaves the others alone.
        if (!string.Equals(target.VerificationSnapshot?.Status, AiVerificationStatus.Verified.ToString(), StringComparison.Ordinal))
        {
            return AiConnectionActivationResultDto.Refused("the profile has not been verified since its last change — verify it, then activate");
        }

        if (target.IsActive)
        {
            return AiConnectionActivationResultDto.Success;
        }

        target.IsActive = true;
        target.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        await this.AuditAsync("activated", target, false, ct);
        return AiConnectionActivationResultDto.Success;
    }

    public async Task<bool> DeactivateAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnectionProfiles.FindAsync([connectionId], ct);
        if (record is null)
        {
            return false;
        }

        record.IsActive = false;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(ct);
        await this.AuditAsync("deactivated", record, false, ct);
        return true;
    }

    public async Task<bool> SaveVerificationAsync(
        Guid connectionId,
        AiVerificationResultDto verification,
        CancellationToken ct = default)
    {
        var record = await dbContext.AiConnectionProfiles
            .Include(profile => profile.VerificationSnapshot)
            .FirstOrDefaultAsync(profile => profile.Id == connectionId, ct);
        if (record is null)
        {
            return false;
        }

        record.VerificationSnapshot = ToVerificationRecord(connectionId, verification);
        record.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<AiConnectionDto?> GetForTierAsync(
        Guid clientId,
        AiConnectionModelCategory tier,
        CancellationToken ct = default)
    {
        var purpose = tier switch
        {
            AiConnectionModelCategory.LowEffort => AiPurpose.ReviewLowEffort,
            AiConnectionModelCategory.MediumEffort => AiPurpose.ReviewMediumEffort,
            AiConnectionModelCategory.HighEffort => AiPurpose.ReviewHighEffort,
            AiConnectionModelCategory.Embedding => AiPurpose.EmbeddingDefault,
            AiConnectionModelCategory.MemoryReconsideration => AiPurpose.MemoryReconsideration,
            _ => AiPurpose.ReviewDefault,
        };

        var resolved = await this.GetActiveBindingForPurposeAsync(clientId, purpose, ct);
        return resolved?.Connection;
    }

    public async Task<AiResolvedPurposeBindingDto?> GetActiveBindingForPurposeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default)
    {
        var record = await this.WithReadDbAsync(
            db => db.AiConnectionProfiles
                .Include(profile => profile.ConfiguredModels)
                .Include(profile => profile.PurposeBindings)
                .Include(profile => profile.VerificationSnapshot)
                .Where(profile => profile.ClientId == clientId && profile.IsActive)
                .OrderBy(profile => profile.DisplayName)
                .ThenBy(profile => profile.Id)
                .FirstOrDefaultAsync(ct),
            ct);

        if (record is null)
        {
            return null;
        }

        var bindingRecord = FindActiveBindingRecord(record, purpose);

        if (bindingRecord is null)
        {
            return null;
        }

        var modelRecord = record.ConfiguredModels.FirstOrDefault(model => model.Id == bindingRecord.ConfiguredModelId);
        if (modelRecord is null)
        {
            return null;
        }

        return new AiResolvedPurposeBindingDto(this.ToDto(record), ToConfiguredModelDto(modelRecord), ToBindingDto(bindingRecord, modelRecord));
    }

    public async Task<AiResolvedPurposeBindingDto?> GetModelBindingAsync(
        Guid clientId,
        Guid configuredModelId,
        CancellationToken ct = default)
    {
        var records = await this.WithReadDbAsync(
            db => db.AiConnectionProfiles
                .Include(profile => profile.ConfiguredModels)
                .Include(profile => profile.PurposeBindings)
                .Where(profile => profile.ClientId == clientId)
                .ToListAsync(ct),
            ct);

        foreach (var record in records)
        {
            var modelRecord = record.ConfiguredModels.FirstOrDefault(model => model.Id == configuredModelId);
            if (modelRecord is null)
            {
                continue;
            }

            if (!modelRecord.OperationKinds.Contains(AiOperationKind.Chat.ToString(), StringComparer.Ordinal))
            {
                return null;
            }

            // Reuse an existing binding's protocol mode for this model so the pass runs on the same wire protocol
            // the model was configured with; otherwise fall back to Auto (the driver only reads the protocol mode).
            var existingBinding = record.PurposeBindings
                                      .FirstOrDefault(binding => binding.ConfiguredModelId == configuredModelId && binding.IsEnabled)
                                  ?? record.PurposeBindings.FirstOrDefault(binding => binding.ConfiguredModelId == configuredModelId);
            // Fall back to Auto when there is no binding or the persisted protocol mode is not a known value,
            // so an unexpected stored string cannot abort resolution for the whole model.
            var protocolMode = existingBinding is not null
                               && Enum.TryParse<AiProtocolMode>(existingBinding.ProtocolMode, true, out var parsedProtocolMode)
                ? parsedProtocolMode
                : AiProtocolMode.Auto;

            var synthesizedBinding = new AiPurposeBindingDto(
                Guid.NewGuid(),
                AiPurpose.ReviewDefault,
                modelRecord.Id,
                modelRecord.RemoteModelId,
                protocolMode);

            return new AiResolvedPurposeBindingDto(this.ToDto(record), ToConfiguredModelDto(modelRecord), synthesizedBinding);
        }

        return null;
    }

    // Provider configuration is where credentials and spend authority live, so every change to it is recorded
    // against the owning tenant. Called after the write has been committed: the audit is a record of what
    // happened, and an audit failure must not undo a change already reported as successful.
    private async Task AuditAsync(
        string action,
        AiConnectionProfileRecord record,
        bool credentialChanged,
        CancellationToken ct)
    {
        if (configAudit is null)
        {
            return;
        }

        await configAudit.RecordAsync(
            new AiProviderConfigAuditEntry(
                action,
                record.Id,
                record.DisplayName,
                Enum.TryParse<AiProviderKind>(record.ProviderKind, true, out var providerKind)
                    ? providerKind
                    : default,
                record.BaseUrl,
                record.ClientId,
                record.TenantId,
                credentialChanged),
            ct);
    }

    // A tenant's provider-kind policy is enforced when a profile is written as well as when one is used, so a
    // forbidden provider cannot be configured and then discovered mid-review. The refusal names what is
    // permitted, because an operator staring at a rejected form needs to know what to choose instead.
    private async Task GuardProviderPolicyForClientAsync(
        Guid clientId,
        AiProviderKind providerKind,
        string baseUrl,
        CancellationToken ct)
    {
        if (providerPolicies is null || clientId == Guid.Empty)
        {
            return;
        }

        Throw(await providerPolicies.GetForClientAsync(clientId, ct), providerKind, baseUrl);
    }

    private async Task GuardProviderPolicyForTenantAsync(
        Guid tenantId,
        AiProviderKind providerKind,
        string baseUrl,
        CancellationToken ct)
    {
        if (providerPolicies is null || tenantId == Guid.Empty)
        {
            return;
        }

        Throw(await providerPolicies.GetForTenantAsync(tenantId, ct), providerKind, baseUrl);
    }

    private static void Throw(TenantProviderPolicy policy, AiProviderKind providerKind, string baseUrl)
    {
        if (policy.DescribeRefusal(providerKind) is { } kindRefusal)
        {
            throw new ProviderKindNotPermittedException(providerKind, kindRefusal);
        }

        // Where the traffic goes is the half a provider family cannot answer: a family reached at an
        // operator-supplied base URL constrains nothing by itself.
        if (policy.DescribeEndpointRefusal(baseUrl) is { } endpointRefusal)
        {
            throw new ProviderKindNotPermittedException(providerKind, endpointRefusal);
        }
    }

    // A credential is stored as one Data-Protection-wrapped blob whose inside is an envelope, so the schema never
    // learns what a given provider's credential is made of. An API key is one string today; SigV4 needs three and
    // a Google service account is a JSON document, and those arrive with their drivers without touching either
    // this method or the column.
    private string? ProtectSecret(string? secret, AiAuthMode authMode)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }

        // A caller that already holds a multi-field envelope passes it through; anything else is the single
        // string an API-key mode uses.
        var envelope = ProviderSecretEnvelope.Decode(secret, authMode);
        return secretProtectionCodec.Protect(envelope.Encode(), SecretPurpose);
    }

    // Returns what a driver needs: the single value for the modes whose credential is one string, and the encoded
    // envelope for the modes whose credential is not, which their driver decodes. Rows written before the
    // envelope existed hold a bare string and read back unchanged.
    private string? UnprotectSecret(string? secret, AiAuthMode authMode)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }

        var envelope = ProviderSecretEnvelope.Decode(secretProtectionCodec.Unprotect(secret, SecretPurpose), authMode);
        return envelope.SingleValue ?? (envelope.Fields.Count == 0 ? null : envelope.Encode());
    }

    private async Task<TResult> WithReadDbAsync<TResult>(
        Func<MeisterProPRDbContext, Task<TResult>> operation,
        CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }

    private AiConnectionDto ToDto(AiConnectionProfileRecord record)
    {
        var models = record.ConfiguredModels
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.RemoteModelId, StringComparer.OrdinalIgnoreCase)
            .Select(ToConfiguredModelDto)
            .ToList()
            .AsReadOnly();

        var bindings = record.PurposeBindings
            .OrderBy(binding => binding.Purpose, StringComparer.Ordinal)
            .Select(binding => ToBindingDto(binding, record.ConfiguredModels.First(model => model.Id == binding.ConfiguredModelId)))
            .ToList()
            .AsReadOnly();

        return new AiConnectionDto(
            record.Id,
            record.ClientId,
            record.DisplayName,
            Enum.Parse<AiProviderKind>(record.ProviderKind, true),
            record.BaseUrl,
            Enum.Parse<AiAuthMode>(record.AuthMode, true),
            Enum.Parse<AiDiscoveryMode>(record.DiscoveryMode, true),
            record.IsActive,
            models,
            bindings,
            ToVerificationDto(record.VerificationSnapshot),
            record.CreatedAt,
            record.UpdatedAt,
            NormalizeMap(record.DefaultHeaders),
            NormalizeMap(record.DefaultQueryParams),
            this.UnprotectSecret(record.ProtectedSecret, Enum.Parse<AiAuthMode>(record.AuthMode, true)),
            record.TenantId);
    }

    private static AiConfiguredModelDto ToConfiguredModelDto(AiConfiguredModelRecord record)
    {
        return new AiConfiguredModelDto(
            record.Id,
            record.RemoteModelId,
            record.DisplayName,
            record.OperationKinds.Select(kind => Enum.Parse<AiOperationKind>(kind, true)).ToList().AsReadOnly(),
            record.SupportedProtocolModes.Select(mode => Enum.Parse<AiProtocolMode>(mode, true)).ToList().AsReadOnly(),
            record.TokenizerName,
            record.MaxInputTokens,
            record.EmbeddingDimensions,
            record.SupportsStructuredOutput,
            record.SupportsToolUse,
            Enum.Parse<AiConfiguredModelSource>(record.Source, true),
            record.LastSeenAt,
            record.InputCostPer1MUsd,
            record.OutputCostPer1MUsd,
            record.MaxContextTokens,
            record.CachedInputCostPer1MUsd,
            record.CacheWriteCostPer1MUsd,
            record.SupportsReasoning,
            record.SupportsPromptCaching,
            record.ReasoningContentField);
    }

    private static AiPurposeBindingDto ToBindingDto(AiPurposeBindingRecord record, AiConfiguredModelRecord model)
    {
        return new AiPurposeBindingDto(
            record.Id,
            Enum.Parse<AiPurpose>(record.Purpose, true),
            record.ConfiguredModelId,
            model.RemoteModelId,
            Enum.Parse<AiProtocolMode>(record.ProtocolMode, true),
            record.IsEnabled,
            record.CreatedAt,
            record.UpdatedAt);
    }

    private static AiVerificationResultDto ToVerificationDto(AiVerificationSnapshotRecord? record)
    {
        if (record is null)
        {
            return AiVerificationResultDto.NeverVerified;
        }

        return new AiVerificationResultDto(
            Enum.Parse<AiVerificationStatus>(record.Status, true),
            string.IsNullOrWhiteSpace(record.FailureCategory)
                ? null
                : Enum.Parse<AiVerificationFailureCategory>(record.FailureCategory, true),
            record.Summary,
            record.ActionHint,
            record.CheckedAt,
            (record.Warnings ?? []).ToList().AsReadOnly(),
            record.DriverMetadata);
    }

    private static AiVerificationSnapshotRecord ToVerificationRecord(Guid connectionId, AiVerificationResultDto verification)
    {
        return new AiVerificationSnapshotRecord
        {
            ConnectionProfileId = connectionId,
            Status = verification.Status.ToString(),
            FailureCategory = verification.FailureCategory?.ToString(),
            Summary = verification.Summary,
            ActionHint = verification.ActionHint,
            CheckedAt = verification.CheckedAt,
            Warnings = (verification.Warnings ?? []).ToArray(),
            DriverMetadata = verification.DriverMetadata is null ? null : NormalizeMap(verification.DriverMetadata),
        };
    }

    private static List<AiConfiguredModelRecord> BuildConfiguredModels(
        Guid connectionId,
        AiConnectionWriteRequestDto request,
        IEnumerable<AiConfiguredModelRecord>? existingRecords)
    {
        var existingByRemoteModelId = (existingRecords ?? [])
            .GroupBy(record => record.RemoteModelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return request.ConfiguredModels
            .GroupBy(model => model.RemoteModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(model =>
            {
                var fallbackRecordId = existingByRemoteModelId.TryGetValue(model.RemoteModelId, out var existingRecord)
                    ? existingRecord.Id
                    : Guid.NewGuid();
                var recordId = model.Id != Guid.Empty ? model.Id : fallbackRecordId;

                return new AiConfiguredModelRecord
                {
                    Id = recordId,
                    ConnectionProfileId = connectionId,
                    RemoteModelId = model.RemoteModelId.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.RemoteModelId.Trim() : model.DisplayName.Trim(),
                    OperationKinds = model.OperationKinds.Select(kind => kind.ToString()).Distinct(StringComparer.Ordinal).ToArray(),
                    SupportedProtocolModes = model.SupportedProtocolModes.Select(mode => mode.ToString()).Distinct(StringComparer.Ordinal).ToArray(),
                    TokenizerName = string.IsNullOrWhiteSpace(model.TokenizerName) ? null : model.TokenizerName.Trim(),
                    MaxInputTokens = model.MaxInputTokens,
                    MaxContextTokens = model.MaxContextTokens,
                    EmbeddingDimensions = model.EmbeddingDimensions,
                    SupportsStructuredOutput = model.SupportsStructuredOutput,
                    SupportsToolUse = model.SupportsToolUse,
                    Source = model.Source.ToString(),
                    LastSeenAt = model.LastSeenAt,
                    InputCostPer1MUsd = model.InputCostPer1MUsd,
                    OutputCostPer1MUsd = model.OutputCostPer1MUsd,
                    CachedInputCostPer1MUsd = model.CachedInputCostPer1MUsd,
                    CacheWriteCostPer1MUsd = model.CacheWriteCostPer1MUsd,
                    SupportsReasoning = model.SupportsReasoning,
                    SupportsPromptCaching = model.SupportsPromptCaching,
                    ReasoningContentField = string.IsNullOrWhiteSpace(model.ReasoningContentField)
                        ? null
                        : model.ReasoningContentField.Trim(),
                };
            })
            .ToList();
    }

    private static List<AiPurposeBindingRecord> BuildPurposeBindings(
        Guid connectionId,
        IReadOnlyList<AiPurposeBindingDto> bindings,
        IReadOnlyList<AiConfiguredModelRecord> configuredModels,
        DateTimeOffset now)
    {
        var modelsById = configuredModels.ToDictionary(model => model.Id);
        var modelsByRemoteModelId = configuredModels.ToDictionary(model => model.RemoteModelId, StringComparer.OrdinalIgnoreCase);

        return bindings
            .GroupBy(binding => binding.Purpose)
            .Select(group => group.First())
            .Select(binding =>
            {
                var fallbackModelId = binding.RemoteModelId is not null && modelsByRemoteModelId.TryGetValue(binding.RemoteModelId, out var remoteModel)
                    ? remoteModel.Id
                    : Guid.Empty;
                var modelId = binding.ConfiguredModelId.HasValue && binding.ConfiguredModelId.Value != Guid.Empty
                    ? binding.ConfiguredModelId.Value
                    : fallbackModelId;

                if (!modelsById.ContainsKey(modelId))
                {
                    throw new InvalidOperationException($"Purpose binding '{binding.Purpose}' references an unknown configured model.");
                }

                return new AiPurposeBindingRecord
                {
                    Id = binding.Id == Guid.Empty ? Guid.NewGuid() : binding.Id,
                    ConnectionProfileId = connectionId,
                    ConfiguredModelId = modelId,
                    Purpose = binding.Purpose.ToString(),
                    ProtocolMode = binding.ProtocolMode.ToString(),
                    IsEnabled = binding.IsEnabled,
                    CreatedAt = binding.CreatedAt ?? now,
                    UpdatedAt = now,
                };
            })
            .ToList();
    }

    private static AiPurposeBindingRecord? FindActiveBindingRecord(AiConnectionProfileRecord record, AiPurpose purpose)
    {
        var binding = record.PurposeBindings.FirstOrDefault(candidate =>
            string.Equals(candidate.Purpose, purpose.ToString(), StringComparison.Ordinal) && candidate.IsEnabled);

        if (binding is not null)
        {
            return binding;
        }

        // Walk the fallback chain so an unbound purpose resolves to its cheaper relative rather than nothing.
        return FallbackPurpose(purpose) is { } fallbackPurpose
            ? FindActiveBindingRecord(record, fallbackPurpose)
            : null;
    }

    private static AiPurpose? FallbackPurpose(AiPurpose purpose)
    {
        return purpose switch
        {
            // Cheap per-file triage falls back to the low-effort review model (which itself falls back to
            // ReviewDefault below), so the model still judges complexity on a cheap model when no dedicated
            // triage binding is configured — instead of silently dropping to the size heuristic.
            AiPurpose.ReviewTriage => AiPurpose.ReviewLowEffort,
            // Evidence-gathering verification falls back to the cheap triage model (then to low-effort →
            // default), so verification runs on an independent, inexpensive model rather than self-verifying
            // on the reviewer's model when no dedicated verification binding is configured.
            AiPurpose.ReviewVerification => AiPurpose.ReviewTriage,
            AiPurpose.ProRVPrefilter
                or AiPurpose.ReviewLowEffort
                or AiPurpose.ReviewMediumEffort
                or AiPurpose.ReviewHighEffort => AiPurpose.ReviewDefault,
            _ => null,
        };
    }

    private static bool IsBindingValid(AiPurpose purpose, AiConfiguredModelRecord model, AiPurposeBindingRecord binding)
    {
        var supportsChat = model.OperationKinds.Contains(AiOperationKind.Chat.ToString(), StringComparer.Ordinal);
        var supportsEmbedding = model.OperationKinds.Contains(AiOperationKind.Embedding.ToString(), StringComparer.Ordinal);

        if (purpose == AiPurpose.EmbeddingDefault)
        {
            if (!supportsEmbedding || !model.EmbeddingDimensions.HasValue || model.EmbeddingDimensions.Value <= 0)
            {
                return false;
            }

            return string.Equals(binding.ProtocolMode, AiProtocolMode.Auto.ToString(), StringComparison.Ordinal) ||
                   string.Equals(binding.ProtocolMode, AiProtocolMode.Embeddings.ToString(), StringComparison.Ordinal);
        }

        if (!supportsChat)
        {
            return false;
        }

        return string.Equals(binding.ProtocolMode, AiProtocolMode.Auto.ToString(), StringComparison.Ordinal) ||
               model.SupportedProtocolModes.Contains(binding.ProtocolMode, StringComparer.Ordinal);
    }

    private bool RequiresVerificationReset(
        AiConnectionProfileRecord record,
        AiConnectionWriteRequestDto request,
        IReadOnlyList<AiConfiguredModelRecord> updatedModels,
        IReadOnlyList<AiPurposeBindingRecord> updatedBindings)
    {
        if (!string.Equals(record.ProviderKind, request.ProviderKind.ToString(), StringComparison.Ordinal) ||
            !string.Equals(record.BaseUrl, request.BaseUrl, StringComparison.Ordinal) ||
            !string.Equals(record.AuthMode, request.AuthMode.ToString(), StringComparison.Ordinal) ||
            !DictionaryEquals(record.DefaultHeaders, NormalizeMap(request.DefaultHeaders)) ||
            !DictionaryEquals(record.DefaultQueryParams, NormalizeMap(request.DefaultQueryParams)))
        {
            return true;
        }

        var requestedSecret = request.Secret;
        var existingSecret = this.UnprotectSecret(record.ProtectedSecret, request.AuthMode);
        if (!string.Equals(existingSecret, requestedSecret, StringComparison.Ordinal))
        {
            return true;
        }

        return !ConfiguredModelsEqual(record.ConfiguredModels, updatedModels) ||
               !PurposeBindingsEqual(record.PurposeBindings, updatedBindings);
    }

    private static bool ConfiguredModelsEqual(
        IEnumerable<AiConfiguredModelRecord> current,
        IEnumerable<AiConfiguredModelRecord> updated)
    {
        var currentList = current.ToList();
        var updatedList = updated.ToList();
        if (currentList.Count != updatedList.Count)
        {
            return false;
        }

        var currentByRemoteModelId = currentList.ToDictionary(model => model.RemoteModelId, StringComparer.OrdinalIgnoreCase);
        foreach (var model in updatedList)
        {
            if (!currentByRemoteModelId.TryGetValue(model.RemoteModelId, out var existing))
            {
                return false;
            }

            if (!string.Equals(existing.DisplayName, model.DisplayName, StringComparison.Ordinal) ||
                !SequenceEqual(existing.OperationKinds, model.OperationKinds) ||
                !SequenceEqual(existing.SupportedProtocolModes, model.SupportedProtocolModes) ||
                !string.Equals(existing.TokenizerName, model.TokenizerName, StringComparison.Ordinal) ||
                existing.MaxInputTokens != model.MaxInputTokens ||
                existing.MaxContextTokens != model.MaxContextTokens ||
                existing.EmbeddingDimensions != model.EmbeddingDimensions ||
                existing.SupportsStructuredOutput != model.SupportsStructuredOutput ||
                existing.SupportsToolUse != model.SupportsToolUse ||
                !string.Equals(existing.Source, model.Source, StringComparison.Ordinal) ||
                existing.LastSeenAt != model.LastSeenAt ||
                existing.InputCostPer1MUsd != model.InputCostPer1MUsd ||
                existing.OutputCostPer1MUsd != model.OutputCostPer1MUsd ||
                existing.CachedInputCostPer1MUsd != model.CachedInputCostPer1MUsd
                || existing.CacheWriteCostPer1MUsd != model.CacheWriteCostPer1MUsd
                || existing.SupportsReasoning != model.SupportsReasoning
                || existing.SupportsPromptCaching != model.SupportsPromptCaching
                || existing.ReasoningContentField != model.ReasoningContentField)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PurposeBindingsEqual(
        IEnumerable<AiPurposeBindingRecord> current,
        IEnumerable<AiPurposeBindingRecord> updated)
    {
        var currentList = current.ToList();
        var updatedList = updated.ToList();

        if (currentList.Count != updatedList.Count)
        {
            return false;
        }

        var currentByPurpose = currentList.ToDictionary(binding => binding.Purpose, StringComparer.Ordinal);
        foreach (var binding in updatedList)
        {
            if (!currentByPurpose.TryGetValue(binding.Purpose, out var existing))
            {
                return false;
            }

            if (existing.ConfiguredModelId != binding.ConfiguredModelId ||
                !string.Equals(existing.ProtocolMode, binding.ProtocolMode, StringComparison.Ordinal) ||
                existing.IsEnabled != binding.IsEnabled)
            {
                return false;
            }
        }

        return true;
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var normalizedLeft = NormalizeMap(left);
        var normalizedRight = NormalizeMap(right);
        if (normalizedLeft.Count != normalizedRight.Count)
        {
            return false;
        }

        foreach (var pair in normalizedLeft)
        {
            if (!normalizedRight.TryGetValue(pair.Key, out var value) ||
                !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> NormalizeMap(IReadOnlyDictionary<string, string>? values)
    {
        return values is null
            ? []
            : values
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.First().Key.Trim(), group => group.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public Task<AiConnectionActivationResultDto> ActivateAsync(Guid connectionId, string model, CancellationToken ct = default)
    {
        _ = model;
        return this.ActivateAsync(connectionId, ct);
    }

    public Task<AiConnectionDto> AddAsync(
        Guid clientId,
        string displayName,
        string endpointUrl,
        IReadOnlyList<string> models,
        string? apiKey,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities = null,
        AiConnectionModelCategory? modelCategory = null,
        CancellationToken ct = default)
    {
        var configuredModels = models
            .Select(modelName => new AiConfiguredModelDto(
                Guid.NewGuid(),
                modelName,
                modelName,
                modelCategory == AiConnectionModelCategory.Embedding || modelName.Contains("embedding", StringComparison.OrdinalIgnoreCase)
                    ? [AiOperationKind.Embedding]
                    : [AiOperationKind.Chat],
                modelCategory == AiConnectionModelCategory.Embedding || modelName.Contains("embedding", StringComparison.OrdinalIgnoreCase)
                    ? [AiProtocolMode.Auto, AiProtocolMode.Embeddings]
                    : [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions],
                modelCapabilities?.FirstOrDefault(capability => string.Equals(capability.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                    ?.TokenizerName,
                modelCapabilities?.FirstOrDefault(capability => string.Equals(capability.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                    ?.MaxInputTokens,
                modelCapabilities?.FirstOrDefault(capability => string.Equals(capability.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                    ?.EmbeddingDimensions,
                modelCategory != AiConnectionModelCategory.Embedding,
                modelCategory != AiConnectionModelCategory.Embedding,
                AiConfiguredModelSource.Manual,
                null,
                modelCapabilities?.FirstOrDefault(capability => string.Equals(capability.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                    ?.InputCostPer1MUsd,
                modelCapabilities?.FirstOrDefault(capability => string.Equals(capability.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                    ?.OutputCostPer1MUsd,
                CachedInputCostPer1MUsd: modelCapabilities?.FirstOrDefault(capability => string.Equals(
                        capability.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                    ?.CachedInputCostPer1MUsd))
            .ToList()
            .AsReadOnly();

        var resolvedModel = models.FirstOrDefault();
        var bindings = resolvedModel is null
            ? []
            : new List<AiPurposeBindingDto>
            {
                new(
                    Guid.NewGuid(),
                    modelCategory switch
                    {
                        AiConnectionModelCategory.LowEffort => AiPurpose.ReviewLowEffort,
                        AiConnectionModelCategory.MediumEffort => AiPurpose.ReviewMediumEffort,
                        AiConnectionModelCategory.HighEffort => AiPurpose.ReviewHighEffort,
                        AiConnectionModelCategory.Embedding => AiPurpose.EmbeddingDefault,
                        AiConnectionModelCategory.MemoryReconsideration => AiPurpose.MemoryReconsideration,
                        _ => AiPurpose.ReviewDefault,
                    },
                    null,
                    resolvedModel,
                    modelCategory == AiConnectionModelCategory.Embedding ? AiProtocolMode.Embeddings : AiProtocolMode.Auto),
            }.AsReadOnly();

        return this.AddAsync(
            clientId,
            new AiConnectionWriteRequestDto(
                displayName,
                AiProviderKind.AzureOpenAi,
                endpointUrl,
                string.IsNullOrWhiteSpace(apiKey) ? AiAuthMode.AzureIdentity : AiAuthMode.ApiKey,
                AiDiscoveryMode.ManualOnly,
                configuredModels,
                bindings,
                null,
                null,
                apiKey),
            ct);
    }
}
