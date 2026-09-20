// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for provider-neutral AI connection profiles.</summary>
public sealed class AiConnectionRepository(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    ITenantProviderPolicyProvider providerPolicies,
    IAiProviderDriverRegistry providerDrivers,
    EgressUrlPolicy egressPolicy,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
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

        // Every row in this list belongs to the same client, so the tenant's allow-list is read once for all of
        // them rather than per row.
        var policy = await providerPolicies.GetForClientAsync(clientId, ct);

        return records
            .Select(record => this.ToDto(record, policy))
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

        var policy = await providerPolicies.GetForTenantAsync(tenantId, ct);

        return records
            .Select(record => this.ToDto(record, policy))
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
        return record is null ? null : this.ToDto(record, await providerPolicies.GetForClientAsync(clientId, ct));
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
        return record is null ? null : this.ToDto(record, await this.GetPolicyForAsync(record, ct));
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto> AddAsync(
        Guid clientId,
        AiConnectionWriteRequestDto request,
        CancellationToken ct = default)
    {
        var policy = await this.GuardProviderPolicyForClientAsync(clientId, request.ProviderKind, request.BaseUrl, ct);

        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuredModels = this.BuildConfiguredModels(profileId, request, null);
        var bindings = this.BuildPurposeBindings(profileId, request, configuredModels, null, now);
        var declaredValues = MergeDeclaredValues(null, request.ProviderSettings);
        this.RefuseDeclaredEgress(request.ProviderKind, declaredValues);

        var record = new AiConnectionProfileRecord
        {
            Id = profileId,
            ClientId = clientId,
            DisplayName = request.DisplayName,
            ProviderKind = this.IdentityToStore(null, request.ProviderKind),
            BaseUrl = request.BaseUrl,
            AuthMode = this.AuthModeToStore(null, request.ProviderKind, request.AuthMode),
            ProtectedSecret = this.ComposeProtectedSecret(null, request, this.DeclaringKey(request.ProviderKind)),
            DefaultHeaders = NormalizeMap(request.DefaultHeaders),
            DefaultQueryParams = NormalizeMap(request.DefaultQueryParams),
            ProviderSettings = declaredValues,
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
        await this.AuditAsync("created", record, CarriesCredentialMaterial(request), ct);
        return this.ToDto(record, policy);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto> AddTenantAsync(Guid tenantId, AiConnectionWriteRequestDto request, CancellationToken ct = default)
    {
        var policy = await this.GuardProviderPolicyForTenantAsync(tenantId, request.ProviderKind, request.BaseUrl, ct);

        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuredModels = this.BuildConfiguredModels(profileId, request, null);
        var bindings = this.BuildPurposeBindings(profileId, request, configuredModels, null, now);
        var declaredValues = MergeDeclaredValues(null, request.ProviderSettings);
        this.RefuseDeclaredEgress(request.ProviderKind, declaredValues);

        var record = new AiConnectionProfileRecord
        {
            Id = profileId,
            ClientId = null,
            TenantId = tenantId,
            DisplayName = request.DisplayName,
            ProviderKind = this.IdentityToStore(null, request.ProviderKind),
            BaseUrl = request.BaseUrl,
            AuthMode = this.AuthModeToStore(null, request.ProviderKind, request.AuthMode),
            ProtectedSecret = this.ComposeProtectedSecret(null, request, this.DeclaringKey(request.ProviderKind)),
            DefaultHeaders = NormalizeMap(request.DefaultHeaders),
            DefaultQueryParams = NormalizeMap(request.DefaultQueryParams),
            ProviderSettings = declaredValues,
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
        await this.AuditAsync("created", record, CarriesCredentialMaterial(request), ct);
        return this.ToDto(record, policy);
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

        // A move to another family takes the departing family's data with it. Its declared settings would be
        // deserialized by the new family against its own field types, and its credential decoded against the
        // wrong shape, so neither is carried across.
        var departingKey = this.DepartingKey(record, request.ProviderKind);

        // The floor runs before anything on the record is touched, so a refused address leaves the stored profile
        // exactly as it was rather than half rewritten in the tracked entity.
        var declaredValues = departingKey is null
            ? MergeDeclaredValues(record.ProviderSettings, request.ProviderSettings)
            : NormalizeDeclaredValues(request.ProviderSettings);
        this.RefuseDeclaredEgress(request.ProviderKind, declaredValues);

        var updatedModels = this.BuildConfiguredModels(record.Id, request, record.ConfiguredModels);
        var now = DateTimeOffset.UtcNow;
        var updatedBindings = this.BuildPurposeBindings(
            record.Id,
            request,
            updatedModels,
            record.PurposeBindings,
            now);

        if (departingKey is not null)
        {
            // Checked first, and before anything is written, so a refusal leaves the connection exactly as it
            // was. Nothing an operator can submit today carries a family's key, because the request names its
            // modes as host enumerations; this is what holds once a family supplies its own vocabulary and the
            // request carries the name it declared. Clearing before the check would overwrite the two values it
            // reads and leave it able to refuse the authentication mode alone.
            RefuseDepartingVocabulary(
                departingKey,
                request.AuthMode.ToString(),
                updatedModels,
                updatedBindings);

            // The protocol modes the departing family owned are then reset to the host-reserved default, which
            // no family owns, rather than the rows being removed: a configured model and a purpose binding
            // survive a change of family, and only the vocabulary on them belonged to the family that left. A
            // mode this request supplied for the arriving family is kept, because it is the family's own.
            ClearDepartingVocabulary(request.ProviderKind, updatedModels, updatedBindings);
        }

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

        // A connection that is changing family keeps none of the vocabulary it held: the authentication mode is
        // named again by the family arriving, as the protocol modes are. Only an edit that stays with the same
        // family is a save whose stored spelling is worth keeping.
        record.AuthMode = this.AuthModeToStore(
            departingKey is null ? record.AuthMode : null,
            request.ProviderKind,
            request.AuthMode);
        record.ProviderKind = this.IdentityToStore(record.ProviderKind, request.ProviderKind);
        record.BaseUrl = request.BaseUrl;
        record.DiscoveryMode = request.DiscoveryMode.ToString();
        record.ProviderSettings = declaredValues;
        record.DefaultHeaders = NormalizeMap(request.DefaultHeaders);
        record.DefaultQueryParams = NormalizeMap(request.DefaultQueryParams);
        record.ProtectedSecret = this.ComposeProtectedSecret(
            departingKey is null ? record.ProtectedSecret : null,
            request,
            this.DeclaringKey(request.ProviderKind));
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
        await this.AuditAsync("updated", record, CarriesCredentialMaterial(request), ct);
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

        // The model and the binding are taken out of the profile projection rather than mapped a second time, so
        // the stored values they hold are read once and reported once, on the profile that carries them.
        //
        // The tenant's allow-list is not consulted here. This path resolves a model for a review and runs once
        // per file, where reading the policy would be a query per file, and the callers that act on the answer
        // read it themselves: AiRuntimeResolver asks before it builds a runtime and AiConnectionScopeGuard asks
        // before a connection is referenced. So the availability reported here describes what the stored row
        // says, and the allow-list leg stays with the two places that enforce it.
        var connection = this.ToDto(record, TenantProviderPolicy.Unrestricted);

        return new AiResolvedPurposeBindingDto(
            connection,
            connection.ConfiguredModels.First(model => model.Id == modelRecord.Id),
            connection.PurposeBindings.First(binding => binding.Id == bindingRecord.Id));
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
            // Fall back to Auto when there is no binding or no loaded family claims the persisted protocol
            // mode, so an unexpected stored string cannot abort resolution for the whole model. The value
            // itself is reported on the profile, which projects the same binding row.
            var protocolMode = existingBinding is not null
                               && providerDrivers
                                   .ResolveProtocolMode(record.ProviderKind, existingBinding.ProtocolMode)
                                   .TryGetValue(out var storedProtocolMode)
                ? storedProtocolMode
                : ProviderDeclaredProtocolModes.Auto;

            var synthesizedBinding = new AiPurposeBindingDto(
                Guid.NewGuid(),
                AiPurpose.ReviewDefault,
                modelRecord.Id,
                modelRecord.RemoteModelId,
                protocolMode);

            // Resolution path: the allow-list is read by the callers that enforce it, not once per file here.
            var connection = this.ToDto(record, TenantProviderPolicy.Unrestricted);

            return new AiResolvedPurposeBindingDto(
                connection,
                connection.ConfiguredModels.First(model => model.Id == modelRecord.Id),
                synthesizedBinding);
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
                // The stored identity, not a conversion of it: an entry that named the enum's first member for a
                // family this build cannot name would tell an operator the change happened on a provider it did
                // not happen on.
                record.ProviderKind,
                record.BaseUrl,
                record.ClientId,
                record.TenantId,
                credentialChanged),
            ct);
    }

    // A tenant's provider-kind policy is enforced when a profile is written as well as when one is used, so a
    // forbidden provider cannot be configured and then discovered mid-review. The refusal names what is
    // permitted, because an operator staring at a rejected form needs to know what to choose instead.
    // Returns the policy it read, so the profile the caller goes on to report is described against the same
    // answer the write was checked against.
    private async Task<TenantProviderPolicy> GuardProviderPolicyForClientAsync(
        Guid clientId,
        string providerKind,
        string baseUrl,
        CancellationToken ct)
    {
        if (clientId == Guid.Empty)
        {
            return TenantProviderPolicy.Unrestricted;
        }

        var policy = await providerPolicies.GetForClientAsync(clientId, ct);
        this.Throw(policy, providerKind, baseUrl);
        return policy;
    }

    private async Task<TenantProviderPolicy> GuardProviderPolicyForTenantAsync(
        Guid tenantId,
        string providerKind,
        string baseUrl,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
        {
            return TenantProviderPolicy.Unrestricted;
        }

        var policy = await providerPolicies.GetForTenantAsync(tenantId, ct);
        this.Throw(policy, providerKind, baseUrl);
        return policy;
    }

    // A profile is governed by the tenant its client belongs to, or by its own tenant when it is tenant-scoped.
    // Read from the record rather than from a caller-supplied identifier, so a profile reached by id alone is
    // described against the same policy as the same profile reached through a list. A row carrying neither owner
    // reads as unrestricted, which both write guards do with an empty identifier; looking a policy up for
    // Guid.Empty would describe such a row against whichever tenant that lookup happened to answer with.
    private async Task<TenantProviderPolicy> GetPolicyForAsync(AiConnectionProfileRecord record, CancellationToken ct)
    {
        if (record.ClientId is { } clientId && clientId != Guid.Empty)
        {
            return await providerPolicies.GetForClientAsync(clientId, ct);
        }

        return record.TenantId is { } tenantId && tenantId != Guid.Empty
            ? await providerPolicies.GetForTenantAsync(tenantId, ct)
            : TenantProviderPolicy.Unrestricted;
    }

    private void Throw(TenantProviderPolicy policy, string providerKind, string baseUrl)
    {
        if (policy.GetRefusalReason(providerKind) is { } kindRefusal)
        {
            throw new ProviderKindNotPermittedException(providerKind, kindRefusal);
        }

        // Where the traffic goes is the half a provider family cannot answer for itself: a family reached at an
        // operator-supplied base URL constrains nothing by itself, and one whose endpoint is fixed by its vendor
        // has no base URL for the tenant's list to read, so both halves of the set are checked here.
        var reachRefusal = policy.DescribeReachRefusal(baseUrl, providerDrivers.ReachedHostPatterns(providerKind));
        if (reachRefusal is not null)
        {
            throw new ProviderKindNotPermittedException(providerKind, reachRefusal);
        }
    }

    // Every address the family declared is checked against this installation's egress rules again here, on the
    // way to the column. The same check ran where the request arrived; it runs again at the point of storage so a
    // caller that reaches the repository by another route cannot store a value the installation refuses.
    private void RefuseDeclaredEgress(string providerKind, IReadOnlyDictionary<string, string>? values)
    {
        if (values is not { Count: > 0 } || !providerDrivers.IsRegistered(providerKind))
        {
            return;
        }

        var declaration = providerDrivers.GetRequired(providerKind).Declaration;
        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(declaration, values, egressPolicy);
        if (refusals.Count > 0)
        {
            throw new ProviderDeclaredValueRefusedException(refusals[0].Key, refusals[0].Value);
        }
    }

    // What a console needs to render this family's form, capped and scrubbed of the credential values this
    // connection holds, because every string in it was written by the family.
    private IReadOnlyList<AiDeclaredFieldDto> DescribeDeclaredFields(
        string providerKind,
        ProviderSecretEnvelope? storedSecret)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return [];
        }

        var secrets = storedSecret is null
            ? []
            : storedSecret.Fields.Values.Concat(storedSecret.DeclaredSecrets.Values);

        return ProviderDeclaredFieldProjection.Describe(
            providerDrivers.GetRequired(providerKind).Declaration,
            secrets);
    }

    // The operations a console offers against this connection. Carried on the connection rather than on the
    // family, because what an operator has to be told before starting one is composed from how this connection
    // is configured as well as from what the family declared.
    private IReadOnlyList<AiDeclaredActionDto> DescribeDeclaredActions(
        string providerKind,
        IReadOnlyDictionary<string, string>? providerSettings,
        ProviderSecretEnvelope? storedSecret)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return [];
        }

        var secrets = storedSecret is null
            ? []
            : storedSecret.Fields.Values.Concat(storedSecret.DeclaredSecrets.Values);

        return ProviderDeclaredActionProjection.Describe(
            providerDrivers.GetRequired(providerKind).Declaration,
            providerSettings,
            secrets);
    }

    // Which of those operations this connection is offered, as its family decides from the state the connection
    // is in. A family that says nothing offers every action it declared. An id that is not declared is dropped
    // here: the console renders from the declaration, so an undeclared id has no label, no inputs and nothing to
    // dispatch against.
    private IReadOnlyList<string> OfferedActionIds(
        string providerKind,
        AiConnectionProfileRecord record,
        ProviderSecretEnvelope? storedSecret,
        AiVerificationStatus verificationStatus)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return [];
        }

        var driver = providerDrivers.GetRequired(providerKind);
        var declared = driver.Declaration.Actions.Select(action => action.Id).ToList();
        if (declared.Count == 0 || driver is not IAiProviderActions runnable)
        {
            return declared;
        }

        var offered = runnable.OfferedActionIds(
            new ProviderConnectionState(
                storedSecret is { Fields.Count: > 0 },
                verificationStatus,
                ResolveCredentialHealth(driver.Declaration, record, storedSecret).Health));

        return [.. declared.Where(id => offered.Contains(id, StringComparer.Ordinal))];
    }

    // The same credential health the health store resolves, composed from the columns this read already loaded
    // rather than by reading the row a second time. A family whose declaration says its connections have no
    // credential health has none, which is the store's rule too: a key an operator typed has nothing to report,
    // while a grant that can be revoked does.
    private static ProviderCredentialHealthState ResolveCredentialHealth(
        ProviderDeclaration declaration,
        AiConnectionProfileRecord record,
        ProviderSecretEnvelope? storedSecret)
    {
        if (!declaration.HasCredentialHealth)
        {
            return ProviderCredentialHealthState.Unknown;
        }

        return ProviderCredentialHealthResolver.Resolve(
            ProviderCredentialHealthResolver.FromVerification(
                record.VerificationSnapshot?.Status,
                record.VerificationSnapshot?.Summary,
                record.VerificationSnapshot?.CheckedAt),
            ProviderCredentialHealthResolver.FromReport(
                record.CredentialHealth,
                record.CredentialHealthCause,
                record.CredentialHealthChangedAt),

            // Nothing on this read has made a call, so there is no failure for the retry stage to have
            // classified.
            runtimeClassification: null,
            storedSecret is { Fields.Count: > 0, ExpiresAt: null });
    }

    // What each of the family's read-only computed fields currently evaluates to, recomputed for this read. The
    // family is handed the non-secret values alone, so a computed value cannot be composed from credential
    // material; what comes back is capped and scrubbed regardless, and an address the installation refuses is
    // reported with its reason rather than dropped, so the operator sees why a form is showing what it shows.
    private IReadOnlyList<AiComputedFieldDto> ComputeDeclaredFields(
        string providerKind,
        IReadOnlyDictionary<string, string>? storedSettings,
        ProviderSecretEnvelope? storedSecret)
    {
        if (!providerDrivers.IsRegistered(providerKind))
        {
            return [];
        }

        var driver = providerDrivers.GetRequired(providerKind);
        var declaration = driver.Declaration;
        var computedFields = declaration.ConnectionFields.Where(field => field.IsComputed).ToList();
        if (computedFields.Count == 0)
        {
            return [];
        }

        var source = EffectiveDeclaredValues(storedSettings, this.StoredDeclaredFields(providerKind))
                     ?? new Dictionary<string, string>();
        var computed = driver.ComputeDeclaredValues(source);
        var secrets = storedSecret is null
            ? []
            : storedSecret.Fields.Values.Concat(storedSecret.DeclaredSecrets.Values);

        return
        [
            .. computedFields
                .Where(field => computed.ContainsKey(field.Name))
                .Select(field =>
                {
                    var value = ProviderMessageGuard.Sanitize(
                        computed[field.Name],
                        secrets,
                        ProviderHostLimits.MaximumFieldTextLength);

                    return new AiComputedFieldDto(
                        field.Name,
                        value,
                        DeclaredUrlFloor.GetRefusalReason(declaration, field, value, egressPolicy));
                }),
        ];
    }

    // The connection configuration the family declares today. A family this build has no driver for declares
    // nothing, so a quarantined connection reports no declared configuration instead of failing to project.
    // Secret-marked and computed fields are excluded: a secret value belongs in the credential envelope, and a
    // computed one is recomputed on every read and never persisted.
    private IReadOnlyList<ProviderDeclaredField> StoredDeclaredFields(string providerKind)
    {
        return providerDrivers.IsRegistered(providerKind)
            ?
            [
                .. providerDrivers.GetRequired(providerKind).Declaration.ConnectionFields
                    .Where(field => !field.IsSecret && !field.IsComputed)
            ]
            : [];
    }

    // The identity key of the family a connection is leaving, or null when the family is not changing. A stored
    // identity the host cannot name still yields a key when it is a well-formed one, so a quarantined connection
    // is repointed with the same clearing as any other.
    private string? DepartingKey(AiConnectionProfileRecord record, string requested)
    {
        var stored = providerDrivers.ResolveIdentity(record.ProviderKind);
        var arriving = providerDrivers.ResolveIdentity(requested);

        if (stored.TryGetKey(out var storedKey)
            && arriving.TryGetKey(out var arrivingKey)
            && ProviderVocabulary.KeysEqual(storedKey, arrivingKey))
        {
            return null;
        }

        return stored.TryGetKey(out var departing)
            ? departing
            : ProviderVocabulary.IsValidIdentityKey(record.ProviderKind)
                ? record.ProviderKind
                : null;
    }

    // Refuses a move that would leave any of the vocabulary locations holding a value the departing family
    // qualified. Nothing has been written when this runs, so a refusal leaves the connection as it was, and it
    // names where the value sits because that is what the operator has to clear.
    internal static void RefuseDepartingVocabulary(
        string departingKey,
        string authMode,
        IReadOnlyList<AiConfiguredModelRecord> models,
        IReadOnlyList<AiPurposeBindingRecord> bindings)
    {
        RefuseQualifiedValue(departingKey, "the authentication mode", authMode);

        foreach (var model in models)
        {
            foreach (var mode in model.SupportedProtocolModes)
            {
                RefuseQualifiedValue(departingKey, $"the supported protocol modes of '{model.RemoteModelId}'", mode);
            }
        }

        foreach (var binding in bindings)
        {
            RefuseQualifiedValue(departingKey, $"the protocol mode of the '{binding.Purpose}' binding", binding.ProtocolMode);
        }
    }

    private static void RefuseQualifiedValue(string departingKey, string location, string value)
    {
        if (ProviderVocabulary.KeysEqual(ProviderVocabulary.Split(value).Key, departingKey))
        {
            throw new ProviderRepointNotClearedException(location, departingKey, value);
        }
    }

    // Resets the two protocol-mode locations to the host-reserved default. It is unqualified, no family declares
    // it, and every driver speaks it, so a connection that has just changed family holds a protocol mode the new
    // family can serve rather than one the old family named.
    private static void ClearDepartingVocabulary(
        string arrivingKey,
        IReadOnlyList<AiConfiguredModelRecord> models,
        IReadOnlyList<AiPurposeBindingRecord> bindings)
    {
        foreach (var model in models)
        {
            var kept = model.SupportedProtocolModes.Where(mode => OwnedByArriving(arrivingKey, mode)).ToArray();
            model.SupportedProtocolModes = kept.Length > 0 ? kept : [ProviderDeclaredProtocolModes.Auto];
        }

        foreach (var binding in bindings)
        {
            if (!OwnedByArriving(arrivingKey, binding.ProtocolMode))
            {
                binding.ProtocolMode = ProviderDeclaredProtocolModes.Auto;
            }
        }
    }

    // A mode qualified by the arriving family's key was translated from this request a few lines above and is
    // the family's own to keep. Anything else names the family that left, or names nobody, and the
    // host-reserved default replaces it.
    private static bool OwnedByArriving(string arrivingKey, string? mode)
    {
        return ProviderVocabulary.KeysEqual(ProviderVocabulary.Split(mode).Key, arrivingKey);
    }

    // The submitted declared values on their own, with nothing carried over. Used where a connection changes
    // family: a value the departing family wrote names a field the arriving family did not declare, and the two
    // are free to give one name two meanings.
    private static Dictionary<string, string>? NormalizeDeclaredValues(IReadOnlyDictionary<string, string>? submitted)
    {
        return submitted is not { Count: > 0 }
            ? null
            : new Dictionary<string, string>(submitted, StringComparer.Ordinal);
    }

    // The stored document with the submitted values written over it. A name the request does not carry keeps what
    // was stored, so a field hidden by its visibility condition is left alone rather than cleared, and a value
    // belonging to a field the family no longer declares stays where it is: a family version that is rolled back
    // finds its value again. Null when the result is empty, so "declares nothing" and "left everything empty" stay
    // distinguishable in the column.
    private static Dictionary<string, string>? MergeDeclaredValues(
        IReadOnlyDictionary<string, string>? stored,
        IReadOnlyDictionary<string, string>? submitted)
    {
        var merged = stored is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(stored, StringComparer.Ordinal);

        foreach (var (name, value) in submitted ?? new Dictionary<string, string>())
        {
            merged[name] = value;
        }

        return merged.Count == 0 ? null : merged;
    }

    // What the family and the form read: the stored value of every field the family declares today, with the
    // declared default standing in where nothing is stored. The default is applied here rather than written at
    // creation, so a family that changes its default changes what an unset connection uses, and the column keeps
    // only what an operator actually set.
    private static IReadOnlyDictionary<string, string>? EffectiveDeclaredValues(
        IReadOnlyDictionary<string, string>? stored,
        IReadOnlyList<ProviderDeclaredField> declaredFields)
    {
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in declaredFields)
        {
            if (stored is not null && stored.TryGetValue(field.Name, out var value))
            {
                effective[field.Name] = value;
            }
            else if (field.DefaultValue is not null)
            {
                effective[field.Name] = field.DefaultValue;
            }
        }

        return effective.Count == 0 ? null : effective;
    }

    // What the family says about itself, or null where this build has no driver for it.
    private ProviderDeclaration? DeclarationOf(string providerKind)
    {
        return providerDrivers.IsRegistered(providerKind)
            ? providerDrivers.GetRequired(providerKind).Declaration
            : null;
    }

    // The identity key of the family a connection is stored against, or null where this build has no driver for
    // it. It travels with the secret-marked declared values so one family's value is never handed to another
    // under the same field name.
    private string? DeclaringKey(string providerKind)
    {
        return this.DeclarationOf(providerKind)?.Key;
    }

    // One rule for the three axes a family owns on a connection: a stored value the family still answers to is
    // written back exactly as it was read, and only a value naming something else — or nothing, on a create — is
    // replaced by the spelling the family declares now. Rewriting a value that already resolves would undo a
    // family's row migration on the first edit after it, since a row is read through the spellings the family
    // supersedes and would be saved back under the host's own name. Repointing a connection to another family
    // still rewrites all three, because the stored values then name the family that left.
    private string IdentityToStore(string? stored, string requested)
    {
        var arriving = providerDrivers.ResolveIdentity(requested);

        return stored is not null
               && providerDrivers.ResolveIdentity(stored).TryGetKey(out var storedKey)
               && arriving.TryGetKey(out var arrivingKey)
               && ProviderVocabulary.KeysEqual(storedKey, arrivingKey)
            ? stored
            : arriving.KeyOr(requested.Trim());
    }

    private string AuthModeToStore(string? stored, string providerKind, string requested)
    {
        var arriving = providerDrivers.ResolveAuthMode(providerKind, requested);

        return stored is not null
               && providerDrivers.ResolveAuthMode(providerKind, stored).TryGetValue(out var storedMode)
               && arriving.TryGetValue(out var arrivingMode)
               && ProviderVocabulary.ValuesEqual(storedMode, arrivingMode)
            ? stored
            : arriving.ValueOr(requested.Trim());
    }

    // The stored spellings are the ones already on the row for that model or binding: any of them that names the
    // requested protocol mode is the spelling to keep, whichever position it sat in. A requested shape the family
    // does not speak, and that the host does not reserve, is stored as it was submitted: it names something this
    // family cannot serve, and the read path reports it rather than replacing it with a shape nobody asked for.
    private string ProtocolModeToStore(
        IReadOnlyList<string>? stored,
        string providerKind,
        string requested)
    {
        var arriving = providerDrivers.ResolveProtocolMode(providerKind, requested);
        if (!arriving.TryGetValue(out var arrivingMode))
        {
            return requested.Trim();
        }

        foreach (var value in stored ?? [])
        {
            if (providerDrivers.ResolveProtocolMode(providerKind, value).TryGetValue(out var storedMode)
                && ProviderVocabulary.ValuesEqual(storedMode, arrivingMode))
            {
                return value;
            }
        }

        return arrivingMode;
    }

    // The stored credential with whatever the request re-entered written into it, as one Data-Protection-wrapped
    // blob whose inside is an envelope, so the schema never learns what a given provider's credential is made of.
    // An API key is one string; a family that declares three fields, or one holding a JSON document, arrives
    // with its driver without touching either this method or the column. A family's secret-marked
    // declared values go in here as well, rather than into the settings column, because that column is stored in
    // plain text and a family that declares a client secret would otherwise write it unencrypted.
    private string? ComposeProtectedSecret(
        string? storedProtectedSecret,
        AiConnectionWriteRequestDto request,
        string? declaringKey)
    {
        // Nothing was re-entered, so the stored blob is left exactly as it is. An edit that changes anything else
        // leaves the credential boxes empty, and rewrapping it for no reason would rewrite a row nothing changed.
        if (!CarriesCredentialMaterial(request))
        {
            return storedProtectedSecret;
        }

        var stored = this.DecodeStored(storedProtectedSecret, request.AuthMode);
        var credential = request.Secret is null
            ? stored
            : ProviderSecretEnvelope.Decode(request.Secret, request.AuthMode);

        var declaredSecrets = MergeStoredDeclaredSecrets(stored, declaringKey, request.DeclaredSecrets);
        if (credential is null && declaredSecrets.Count == 0)
        {
            return null;
        }

        var composed = (credential ?? new ProviderSecretEnvelope(
                request.AuthMode.ToString(),
                new Dictionary<string, string>()))
            with
            {
                DeclaredSecrets = declaredSecrets,

                // Recorded on every write, including one that stores no declared value: the key is what says
                // which family's meaning the credential fields carry, and an envelope missing it is read by
                // whichever family holds the row.
                IdentityKey = declaringKey,
            };

        return secretProtectionCodec.Protect(composed.Encode(), SecretPurpose);
    }

    // Whether the write carries credential material of any kind. A family's secret-marked declared value is
    // credential material as much as the connection's own key is: both are written into the protected blob. The
    // audit entry and ComposeProtectedSecret ask this one question, so a rotation of a declared secret cannot be
    // recorded as a change that left the credential alone.
    private static bool CarriesCredentialMaterial(AiConnectionWriteRequestDto request)
    {
        return request.Secret is not null || request.DeclaredSecrets is { Count: > 0 };
    }

    // The declared secrets the envelope already holds, with the submitted ones written over them. Values stored
    // by another family are dropped rather than carried: a field name means whatever the family that declared it
    // says it means, and reading one family's value as another's is how a credential ends up presented to the
    // wrong provider.
    private static Dictionary<string, string> MergeStoredDeclaredSecrets(
        ProviderSecretEnvelope? stored,
        string? declaringKey,
        IReadOnlyDictionary<string, string>? submitted)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (stored is not null && ReadableBy(stored, declaringKey))
        {
            foreach (var (name, value) in stored.DeclaredSecrets)
            {
                merged[name] = value;
            }
        }

        foreach (var (name, value) in submitted ?? new Dictionary<string, string>())
        {
            merged[name] = value;
        }

        return merged;
    }

    // A family reads an envelope it wrote, and one that records no writer. The second case is what keeps a
    // credential written before the key existed readable, which is the same tolerance the decode path already
    // extends to a row written before the envelope existed.
    private static bool ReadableBy(ProviderSecretEnvelope envelope, string? declaringKey)
    {
        return envelope.WrittenBy(declaringKey);
    }

    // The plain values of the secret-marked fields the family declares today, which it reads back under
    // its own field names. A value another family stored, or one whose field this family no longer declares, is
    // not returned.
    private IReadOnlyDictionary<string, string> ReadDeclaredSecrets(
        ProviderSecretEnvelope? stored,
        string providerKind)
    {
        // A family this build has no driver for declares no field, so there is nothing to read back under and
        // nothing to check the stored names against.
        if (stored is null
            || !providerDrivers.IsRegistered(providerKind)
            || !ReadableBy(stored, this.DeclaringKey(providerKind)))
        {
            return new Dictionary<string, string>();
        }

        var declaredNames = providerDrivers.GetRequired(providerKind).Declaration.ConnectionFields
            .Where(field => field.IsSecret)
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        return stored.DeclaredSecrets
            .Where(pair => declaredNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    // The stored credential, unwrapped and read as the envelope it is. Rows written before the envelope existed
    // hold a bare string and decode as the single-field credential they are.
    private ProviderSecretEnvelope? DecodeStored(string? protectedSecret, string authMode)
    {
        return string.IsNullOrWhiteSpace(protectedSecret)
            ? null
            : ProviderSecretEnvelope.Decode(secretProtectionCodec.Unprotect(protectedSecret, SecretPurpose), authMode);
    }

    // Returns what a driver needs: the single value for the modes whose credential is one string, and the encoded
    // envelope for the modes whose credential is not, which their driver decodes. Rows written before the
    // envelope existed hold a bare string and read back unchanged.
    private string? UnprotectSecret(string? secret, string authMode)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }

        var envelope = this.DecodeStored(secret, authMode)!;
        return envelope.SingleValue ?? ResolveFallbackSecret(envelope);
    }

    private static string? ResolveFallbackSecret(ProviderSecretEnvelope envelope)
    {
        return envelope.Fields.Count == 0 ? null : envelope.Encode();
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

    // Every stored vocabulary value the projection reads goes through here. A value this build has no member
    // for is reported on the profile and stands in as the vocabulary's default member, because an enum parse
    // would throw inside the projection and take out every other row being projected with it. The columns hold
    // unconstrained text, so a value written by a build that declared a member this one does not is already
    // storable.
    private static TEnum ResolveStored<TEnum>(
        string? stored,
        AiConnectionVocabularyField field,
        ICollection<AiUnresolvedValueDto> unresolvedValues)
        where TEnum : struct, Enum
    {
        return Report(AiVocabulary.Resolve<TEnum>(stored), field, unresolvedValues);
    }

    // The shape an outcome named, or the value as it was read when no loaded family claims it. Carried rather
    // than substituted, for the reason the identity is: the profile reports what it holds, so a console lists it
    // and a refusal names the value an operator has to correct. It is reported as unresolved either way.
    private static string Report(
        AiVocabularyResolution resolution,
        AiConnectionVocabularyField field,
        ICollection<AiUnresolvedValueDto> unresolvedValues)
    {
        if (resolution.TryGetValue(out var resolved))
        {
            return resolved;
        }

        unresolvedValues.Add(new AiUnresolvedValueDto(field, resolution.Name));
        return resolution.Name;
    }

    // The member an outcome named, or the enumeration's default with the value reported, for the axes the host
    // owns outright.
    private static TEnum Report<TEnum>(
        AiVocabularyResolution<TEnum> resolution,
        AiConnectionVocabularyField field,
        ICollection<AiUnresolvedValueDto> unresolvedValues)
        where TEnum : struct, Enum
    {
        if (resolution.TryGetValue(out var resolved))
        {
            return resolved;
        }

        unresolvedValues.Add(new AiUnresolvedValueDto(field, resolution.Name));
        return default;
    }

    // A profile is available when a loaded family claims the identity it was stored against, the tenant permits
    // that family and where its traffic would go, and every other stored value on the profile resolved. Each of
    // the four reasons it can fail names a different remedy, so each is reported on its own.
    private AiConnectionAvailabilityDto DescribeAvailability(
        AiProviderIdentityResolution identity,
        TenantProviderPolicy policy,
        string? baseUrl,
        IEnumerable<AiUnresolvedValueDto> unresolvedValues)
    {
        // The same vocabulary is stored on several rows of one profile, so a value that fails on each of them is
        // reported once: the second occurrence names the same remedy as the first.
        var values = unresolvedValues.Distinct().ToList().AsReadOnly();

        if (!identity.TryGetKey(out var providerKind))
        {
            return new AiConnectionAvailabilityDto(
                AiConnectionAvailabilityState.Unavailable,
                AiConnectionUnavailableReason.ProviderFamilyAbsent,
                identity.Name,
                values);
        }

        if (!policy.IsAllowed(providerKind))
        {
            return new AiConnectionAvailabilityDto(
                AiConnectionAvailabilityState.Unavailable,
                AiConnectionUnavailableReason.ProviderFamilyNotPermitted,
                identity.Name,
                values);
        }

        // Where the traffic goes is checked on the way to the column, and a tenant can narrow its endpoint list
        // after a profile was saved. Runtime resolution asks the same question again and refuses the profile, so
        // it is reported here instead of being advertised as usable until a review fails on it.
        if (policy.DescribeReachRefusal(baseUrl, providerDrivers.ReachedHostPatterns(providerKind)) is not null)
        {
            return new AiConnectionAvailabilityDto(
                AiConnectionAvailabilityState.Unavailable,
                AiConnectionUnavailableReason.EndpointNotPermitted,
                null,
                values);
        }

        return values.Count == 0
            ? AiConnectionAvailabilityDto.Available
            : new AiConnectionAvailabilityDto(
                AiConnectionAvailabilityState.Unavailable,
                AiConnectionUnavailableReason.StoredValueUnresolved,
                null,
                values);
    }

    private AiConnectionDto ToDto(AiConnectionProfileRecord record, TenantProviderPolicy policy)
    {
        var unresolvedValues = new List<AiUnresolvedValueDto>();

        // The profile is reported with the family it was stored against: the claiming family's key where one
        // claims it, and otherwise the identity exactly as it is stored, so a profile whose add-in is absent
        // round-trips and can be corrected rather than being dropped from the list or rewritten to something
        // else. Resolved through the loaded families, so a connection still holding the spelling its family
        // superseded reports that family's key. It is read first because every other value on the profile
        // belongs to a vocabulary that family owns.
        var identity = providerDrivers.ResolveIdentity(record.ProviderKind);
        var providerKind = identity.KeyOr(identity.Name);

        var models = record.ConfiguredModels
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.RemoteModelId, StringComparer.OrdinalIgnoreCase)
            .Select(model => this.ToConfiguredModelDto(model, providerKind, unresolvedValues))
            .ToList()
            .AsReadOnly();

        var bindings = record.PurposeBindings
            .OrderBy(binding => binding.Purpose, StringComparer.Ordinal)
            .Select(binding => this.ToBindingDto(
                binding,
                record.ConfiguredModels.First(model => model.Id == binding.ConfiguredModelId),
                providerKind,
                unresolvedValues))
            .ToList()
            .AsReadOnly();

        var authMode = Report(
            providerDrivers.ResolveAuthMode(providerKind, record.AuthMode),
            AiConnectionVocabularyField.AuthMode,
            unresolvedValues);

        // A credential another family wrote is not handed to this one. Its fields mean whatever the family that
        // wrote them says they mean, and handing them over is how a service-account document ends up presented
        // as an API key. The connection reports as needing a credential, which it needs.
        var storedSecret = this.DecodeStored(record.ProtectedSecret, authMode);
        if (storedSecret is not null && !ReadableBy(storedSecret, this.DeclaringKey(providerKind)))
        {
            storedSecret = null;
        }

        var discoveryMode = ResolveStored<AiDiscoveryMode>(
            record.DiscoveryMode,
            AiConnectionVocabularyField.DiscoveryMode,
            unresolvedValues);
        var verification = ToVerificationDto(record.VerificationSnapshot, unresolvedValues);

        return new AiConnectionDto(
            record.Id,
            record.ClientId,
            record.DisplayName,
            providerKind,
            record.BaseUrl,
            authMode,
            discoveryMode,
            record.IsActive,
            models,
            bindings,
            verification,
            record.CreatedAt,
            record.UpdatedAt,
            NormalizeMap(record.DefaultHeaders),
            NormalizeMap(record.DefaultQueryParams),
            storedSecret is null ? null : storedSecret.SingleValue ?? ResolveFallbackSecret(storedSecret),
            record.TenantId)
        {
            Availability = this.DescribeAvailability(identity, policy, record.BaseUrl, unresolvedValues),
            ProviderSettings = EffectiveDeclaredValues(record.ProviderSettings, this.StoredDeclaredFields(providerKind)),
            DeclaredSecrets = this.ReadDeclaredSecrets(storedSecret, providerKind),
            DeclaredFields = this.DescribeDeclaredFields(providerKind, storedSecret),
            ComputedFields = this.ComputeDeclaredFields(providerKind, record.ProviderSettings, storedSecret),
            DeclaredActions = this.DescribeDeclaredActions(
                providerKind,
                EffectiveDeclaredValues(record.ProviderSettings, this.StoredDeclaredFields(providerKind)),
                storedSecret),
            OfferedActionIds = this.OfferedActionIds(providerKind, record, storedSecret, verification.Status),
        };
    }

    private AiConfiguredModelDto ToConfiguredModelDto(
        AiConfiguredModelRecord record,
        string providerKind,
        ICollection<AiUnresolvedValueDto> unresolvedValues)
    {
        return new AiConfiguredModelDto(
            record.Id,
            record.RemoteModelId,
            record.DisplayName,
            record.OperationKinds
                .Select(kind => ResolveStored<AiOperationKind>(kind, AiConnectionVocabularyField.OperationKind, unresolvedValues))
                .ToList()
                .AsReadOnly(),
            record.SupportedProtocolModes
                .Select(mode => Report(
                    providerDrivers.ResolveProtocolMode(providerKind, mode),
                    AiConnectionVocabularyField.ProtocolMode,
                    unresolvedValues))
                .ToList()
                .AsReadOnly(),
            record.TokenizerName,
            record.MaxInputTokens,
            record.EmbeddingDimensions,
            record.SupportsStructuredOutput,
            record.SupportsToolUse,
            ResolveStored<AiConfiguredModelSource>(record.Source, AiConnectionVocabularyField.ConfiguredModelSource, unresolvedValues),
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

    private AiPurposeBindingDto ToBindingDto(
        AiPurposeBindingRecord record,
        AiConfiguredModelRecord model,
        string providerKind,
        ICollection<AiUnresolvedValueDto> unresolvedValues)
    {
        return new AiPurposeBindingDto(
            record.Id,
            ResolveStored<AiPurpose>(record.Purpose, AiConnectionVocabularyField.Purpose, unresolvedValues),
            record.ConfiguredModelId,
            model.RemoteModelId,
            Report(
                providerDrivers.ResolveProtocolMode(providerKind, record.ProtocolMode),
                AiConnectionVocabularyField.ProtocolMode,
                unresolvedValues),
            record.IsEnabled,
            record.CreatedAt,
            record.UpdatedAt);
    }

    private static AiVerificationResultDto ToVerificationDto(
        AiVerificationSnapshotRecord? record,
        ICollection<AiUnresolvedValueDto> unresolvedValues)
    {
        if (record is null)
        {
            return AiVerificationResultDto.NeverVerified;
        }

        return new AiVerificationResultDto(
            ResolveStored<AiVerificationStatus>(record.Status, AiConnectionVocabularyField.VerificationStatus, unresolvedValues),
            string.IsNullOrWhiteSpace(record.FailureCategory)
                ? null
                : ResolveStored<AiVerificationFailureCategory>(
                    record.FailureCategory,
                    AiConnectionVocabularyField.VerificationFailureCategory,
                    unresolvedValues),
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

    private List<AiConfiguredModelRecord> BuildConfiguredModels(
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
                    SupportedProtocolModes = model.SupportedProtocolModes
                        .Select(mode => this.ProtocolModeToStore(
                            existingRecord?.SupportedProtocolModes,
                            request.ProviderKind,
                            mode))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
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

    private List<AiPurposeBindingRecord> BuildPurposeBindings(
        Guid connectionId,
        AiConnectionWriteRequestDto request,
        IReadOnlyList<AiConfiguredModelRecord> configuredModels,
        IEnumerable<AiPurposeBindingRecord>? existingRecords,
        DateTimeOffset now)
    {
        var modelsById = configuredModels.ToDictionary(model => model.Id);
        var modelsByRemoteModelId = configuredModels.ToDictionary(model => model.RemoteModelId, StringComparer.OrdinalIgnoreCase);

        // A binding is matched to the stored one by its purpose, which is how an update finds the row it is
        // rewriting everywhere else on this profile.
        var existingByPurpose = (existingRecords ?? [])
            .GroupBy(binding => binding.Purpose, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return request.PurposeBindings
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

                var purpose = binding.Purpose.ToString();
                existingByPurpose.TryGetValue(purpose, out var existingBinding);

                return new AiPurposeBindingRecord
                {
                    Id = binding.Id == Guid.Empty ? Guid.NewGuid() : binding.Id,
                    ConnectionProfileId = connectionId,
                    ConfiguredModelId = modelId,
                    Purpose = purpose,
                    ProtocolMode = this.ProtocolModeToStore(
                        existingBinding is null ? null : [existingBinding.ProtocolMode],
                        request.ProviderKind,
                        binding.ProtocolMode),
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
        // One shared definition of the chain: it is consulted here and by the logical-model role lookup, and two
        // copies would drift into a purpose silently running on a different model than an operator was told.
        return AiPurposeFallbacks.Next(purpose);
    }

    private bool RequiresVerificationReset(
        AiConnectionProfileRecord record,
        AiConnectionWriteRequestDto request,
        IReadOnlyList<AiConfiguredModelRecord> updatedModels,
        IReadOnlyList<AiPurposeBindingRecord> updatedBindings)
    {
        // The family and the authentication mode are compared by what the stored values name, not by how they are
        // spelled: a row holding a spelling its family supersedes, or one qualified by that family's key, means
        // the same family and the same shape as the request that carries the enumerations. Comparing the text
        // would invalidate the verification of every migrated row on the next save, which is a re-verification an
        // operator has to perform by hand. A stored value this build cannot name reads as a change, because
        // nothing says the request still describes what was verified. Both sides of the authentication mode are
        // resolved, because the request carries a spelling of its own: comparing a resolved stored value against
        // an unresolved requested one resets the verification of a save that submitted a valid superseded name.
        var storedIdentity = providerDrivers.ResolveIdentity(record.ProviderKind);
        var requestedIdentity = providerDrivers.ResolveIdentity(request.ProviderKind);
        var storedAuthMode = providerDrivers.ResolveAuthMode(request.ProviderKind, record.AuthMode);
        var requestedAuthMode = providerDrivers.ResolveAuthMode(request.ProviderKind, request.AuthMode);

        if (!storedIdentity.TryGetKey(out var storedKey) ||
            !requestedIdentity.TryGetKey(out var requestedKey) ||
            !ProviderVocabulary.KeysEqual(storedKey, requestedKey) ||
            !storedAuthMode.TryGetValue(out var storedMode) ||
            !requestedAuthMode.TryGetValue(out var requestedMode) ||
            !ProviderVocabulary.ValuesEqual(storedMode, requestedMode) ||
            !string.Equals(record.BaseUrl, request.BaseUrl, StringComparison.Ordinal) ||
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

        // Re-entering a secret-marked declared value replaces what the family presents, so it invalidates the
        // verification exactly as re-entering the credential does.
        if (request.DeclaredSecrets is { Count: > 0 })
        {
            return true;
        }

        // A declared value can decide where the connection goes or what it presents, so changing one invalidates
        // the verification for the same reason changing the base URL does. Compared exactly rather than through
        // the header normalization, because an empty declared value is a value an operator chose.
        if (!DeclaredValuesEqual(record.ProviderSettings, MergeDeclaredValues(record.ProviderSettings, request.ProviderSettings)))
        {
            return true;
        }

        return !ConfiguredModelsEqual(record.ConfiguredModels, updatedModels) ||
               !PurposeBindingsEqual(record.PurposeBindings, updatedBindings);
    }

    private static bool DeclaredValuesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Count == right.Count
               && left.All(pair => right.TryGetValue(pair.Key, out var value)
                                   && string.Equals(pair.Value, value, StringComparison.Ordinal));
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

    // A shape of the Azure OpenAI family, which is the family this overload creates a connection for. Composed
    // rather than read from the family's declaration, because this overload builds the request before any driver
    // is resolved.
    private static string AzureShape(string modeName)
    {
        return ProviderVocabulary.Compose(EvaluationAiConnection.AzureOpenAiKey, modeName);
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
                    ? [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings]
                    : [ProviderDeclaredProtocolModes.Auto, AzureShape("Responses"), AzureShape("ChatCompletions")],
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
                    modelCategory == AiConnectionModelCategory.Embedding
                        ? ProviderDeclaredProtocolModes.Embeddings
                        : ProviderDeclaredProtocolModes.Auto),
            }.AsReadOnly();

        return this.AddAsync(
            clientId,
            new AiConnectionWriteRequestDto(
                displayName,
                EvaluationAiConnection.AzureOpenAiKey,
                endpointUrl,
                string.IsNullOrWhiteSpace(apiKey) ? AzureShape("AzureIdentity") : AzureShape("ApiKey"),
                AiDiscoveryMode.ManualOnly,
                configuredModels,
                bindings,
                null,
                null,
                apiKey),
            ct);
    }
}
