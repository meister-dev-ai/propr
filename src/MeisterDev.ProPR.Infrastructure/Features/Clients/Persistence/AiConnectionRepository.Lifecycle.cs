// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Transport;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Clients.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

public sealed partial class AiConnectionRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AiConnectionDto>> GetByIdsAsync(IReadOnlyCollection<Guid> connectionIds, CancellationToken ct = default)
    {
        var records = await this.WithReadDbAsync(
            db => db.AiConnectionProfiles.AsNoTracking()
                .Include(p => p.ConfiguredModels).Include(p => p.PurposeBindings).Include(p => p.VerificationSnapshot)
                .Where(p => connectionIds.Contains(p.Id)).ToListAsync(ct), ct);
        var clientPolicies = new Dictionary<Guid, TenantProviderPolicy>();
        var tenantPolicies = new Dictionary<Guid, TenantProviderPolicy>();
        var results = new List<AiConnectionDto>();
        foreach (var record in records)
        {
            TenantProviderPolicy policy;
            if (record.TenantId is { } tenantId)
            {
                if (!tenantPolicies.TryGetValue(tenantId, out policy!))
                {
                    tenantPolicies[tenantId] = policy = await providerPolicies.GetForTenantAsync(tenantId, ct);
                }
            }
            else if (record.ClientId is { } ownerClientId)
            {
                if (!clientPolicies.TryGetValue(ownerClientId, out policy!))
                {
                    clientPolicies[ownerClientId] = policy = await providerPolicies.GetForClientAsync(ownerClientId, ct);
                }
            }
            else
            {
                policy = TenantProviderPolicy.Unrestricted;
            }

            results.Add(this.ToDto(record, policy));
        }

        return results;
    }

    private static readonly AiPurpose[] DefaultWorkspacePurposes =
    [
        AiPurpose.ReviewDefault, AiPurpose.ReviewTriage, AiPurpose.ReviewVerification,
        AiPurpose.ReviewLowEffort, AiPurpose.ReviewMediumEffort, AiPurpose.MemoryReconsideration,
    ];

    /// <inheritdoc />
    public async Task<AiVerifiedUpdateResult> VerifyUpdateAsync(
        Guid clientId, AiConnectionDto original,
        AiConnectionWriteRequestDto request, Func<AiConnectionDto, CancellationToken, Task<AiVerificationResultDto>> verify,
        CancellationToken ct = default, Guid? authorizedTenantId = null)
    {
        var candidate = await this.LoadProfileAsync(original.Id, false, ct);
        if (candidate is null || candidate.ClientId != clientId || candidate.TenantId is not null)
        {
            return new(false, NotFound: true);
        }

        if (original.ConfigurationStamp is null || SnapshotStamp(candidate) != original.ConfigurationStamp)
        {
            return new(false, Conflict: true);
        }

        if (!this.IsWorkspaceEditable(candidate))
        {
            return new(false);
        }

        if (request.ConfiguredModels.Any(m => m.Id != Guid.Empty && candidate.ConfiguredModels.All(old => old.Id != m.Id)) ||
            request.PurposeBindings.Any(b => b.Id != Guid.Empty && candidate.PurposeBindings.All(old => old.Id != b.Id)))
        {
            return new(false);
        }

        var owningTenantId = await dbContext.Clients.AsNoTracking().Where(c => c.Id == clientId)
            .Select(c => (Guid?)c.TenantId).SingleOrDefaultAsync(ct);
        if (authorizedTenantId is { } authorizedOwner && owningTenantId != authorizedOwner)
        {
            return new(false, Conflict: true);
        }

        // Candidate changes are detached. Provider calls occur before the promotion transaction starts.
        await this.ApplyUpdateAsync(candidate, request, ct);
        if (!this.IsWorkspaceEditable(candidate))
        {
            return new(false);
        }

        var policy = await providerPolicies.GetForClientAsync(clientId, ct);
        var candidateDto = this.ToDto(candidate, policy);
        await this.GuardCandidateReferencesAsync(candidateDto, ct);
        var verification = await verify(candidateDto, ct);
        if (verification.Status != AiVerificationStatus.Verified)
        {
            return new(false);
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        try
        {
            var current = await this.LoadProfileAsync(original.Id, false, ct);
            if (current is null || current.ClientId != clientId || current.TenantId is not null)
            {
                return new(false, NotFound: true);
            }

            if (SnapshotStamp(current) != original.ConfigurationStamp)
            {
                return new(false, Conflict: true);
            }

            var currentTenantId = await dbContext.Clients.AsNoTracking().Where(c => c.Id == clientId)
                .Select(c => (Guid?)c.TenantId).SingleOrDefaultAsync(ct);
            if (currentTenantId != owningTenantId)
            {
                return new(false, Conflict: true);
            }

            // Rebuild from the fresh snapshot to repeat ownership, provider policy and logical-reference checks.
            this.DetachProfile(current.Id);
            dbContext.Attach(current);
            await this.ApplyUpdateAsync(current, request, ct);
            await this.GuardCandidateReferencesAsync(this.ToDto(current, await providerPolicies.GetForClientAsync(clientId, ct)), ct);
            current.VerificationSnapshot = ToVerificationRecord(current.Id, verification);
            await dbContext.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            await this.AuditAsync("verified-updated", current, CarriesCredentialMaterial(request), ct);
            return new(true, this.ToDto(current, await providerPolicies.GetForClientAsync(clientId, ct)));
        }
        catch (Exception ex) when (IsConcurrentMutation(ex))
        {
            this.DetachProfile(original.Id);
            return new(false, Conflict: true);
        }
    }

    /// <inheritdoc />
    public async Task<AiConfigurationResult> SelectPurposesAsync(
        Guid clientId, AiWorkspacePurposeSelection selection,
        CancellationToken ct = default, Guid? authorizedTenantId = null)
    {
        if (selection.Default is null || selection.High is null || selection.Embedding is null)
        {
            return new(false, "Select Default, High and Embedding models.");
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var profiles = await dbContext.AiConnectionProfiles.AsNoTracking()
            .Include(p => p.ConfiguredModels).Include(p => p.PurposeBindings).Include(p => p.VerificationSnapshot)
            .Where(p => p.ClientId == clientId && p.TenantId == null).ToListAsync(ct);
        try
        {
            var policy = await providerPolicies.GetForClientAsync(clientId, ct);
            var selections = DefaultWorkspacePurposes.ToDictionary(p => p, _ => selection.Default);
            selections[AiPurpose.ReviewHighEffort] = selection.High;
            selections[AiPurpose.EmbeddingDefault] = selection.Embedding;
            foreach (var (purpose, selected) in selections)
            {
                var profile = profiles.FirstOrDefault(p => p.Id == selected.ConnectionId);
                if (profile is null || !this.IsWorkspaceEditable(profile))
                {
                    return new(false, "Select models from connections owned by this client.");
                }

                var dto = this.ToDto(profile, policy);
                var model = dto.ConfiguredModels.FirstOrDefault(m => m.Id == selected.ConfiguredModelId);
                if (dto.Availability.State != AiConnectionAvailabilityState.Available ||
                    dto.Verification.Status != AiVerificationStatus.Verified)
                {
                    return new(false, "Verify every selected connection before applying the workspace models.");
                }

                if (model is null || (purpose == AiPurpose.EmbeddingDefault
                        ? !model.SupportsEmbedding || string.IsNullOrWhiteSpace(model.TokenizerName) ||
                          model.MaxInputTokens is not > 0 || model.EmbeddingDimensions is not (>= 64 and <= 4096)
                        : !model.SupportsChat))
                {
                    return new(
                        false,
                        "Select chat-capable Default and High models and an embedding model with tokenizer, input limit and dimensions from 64 to 4096.");
                }

                if (purpose != AiPurpose.EmbeddingDefault && !string.IsNullOrWhiteSpace(model.ReasoningContentField) &&
                    !string.Equals(
                        model.ReasoningContentField, ReasoningContentRoundTripHandler.ReasoningContentField,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new(false, "Select chat models that require no reasoning field or support reasoning_content across turns.");
                }

                var protocol = purpose == AiPurpose.EmbeddingDefault ? ProviderDeclaredProtocolModes.Embeddings : ProviderDeclaredProtocolModes.Auto;
                if (!ProviderVocabulary.Names(model.SupportedProtocolModes, protocol) ||
                    AiProtocolModeSupport.GetRefusalReason(
                        dto.ProviderKind,
                        providerDrivers.GetRequired(dto.ProviderKind).SupportedProtocolModes, protocol) is not null)
                {
                    return new(false, "Select models that support automatic chat routing or the embeddings protocol.");
                }
            }

            // Direct logical roles precede bindings. Fallback roles are reached only when bindings are absent;
            // this operation creates an exact binding for every selected purpose.
            var roles = await dbContext.ClientPurposeLogicalModels.AsNoTracking()
                .Where(r => r.ClientId == clientId).ToListAsync(ct);
            var overrides = await dbContext.LogicalModelOverrides.AsNoTracking().Where(r => r.ClientId == clientId).ToListAsync(ct);
            var tenantId = await dbContext.Clients.Where(c => c.Id == clientId).Select(c => (Guid?)c.TenantId).SingleOrDefaultAsync(ct);
            if (authorizedTenantId is { } authorizedOwner && tenantId != authorizedOwner)
            {
                return new(false, "Client ownership changed. Reload the workspace before retrying.", true);
            }

            var tenantModels = await dbContext.LogicalModels.AsNoTracking().Where(r => r.TenantId == tenantId).ToListAsync(ct);
            var conflicts = new List<AiPurpose>();
            foreach (var (purpose, selected) in selections)
            {
                var role = roles.FirstOrDefault(r => r.Purpose == purpose);
                if (role is null || string.IsNullOrEmpty(role.LogicalModelName))
                {
                    continue;
                }

                ILogicalModelMapping? mapping = overrides.FirstOrDefault(r => r.Name == role.LogicalModelName)
                                                ?? (ILogicalModelMapping?)tenantModels.FirstOrDefault(r => r.Name == role.LogicalModelName);
                var expectedCapability = purpose == AiPurpose.EmbeddingDefault ? AiOperationKind.Embedding : AiOperationKind.Chat;
                if (mapping is null || mapping.ConnectionId != selected.ConnectionId || mapping.ConfiguredModelId != selected.ConfiguredModelId ||
                    mapping.Capability != expectedCapability)
                {
                    conflicts.Add(purpose);
                }
            }

            if (conflicts.Count > 0)
            {
                return new(
                    false, "Update or remove the conflicting logical purpose mappings in the cell console before selecting these workspace models.", true,
                    conflicts);
            }

            // Managed profiles retain their bindings. An enabled managed binding that would precede a selected
            // binding is refused because changing it would exceed this operation's authority.
            foreach (var (purpose, selected) in selections)
            {
                var competing = profiles.Where(p => p.IsActive && p.PurposeBindings.Any(b => b.IsEnabled && b.Purpose == purpose.ToString()))
                    .Where(p => !this.IsWorkspaceEditable(p));
                var winner = competing.Append(profiles.Single(p => p.Id == selected.ConnectionId))
                    .OrderBy(p => p.DisplayName).ThenBy(p => p.Id).First();
                if (winner.Id != selected.ConnectionId)
                {
                    return new(
                        false, "A managed connection has a higher-priority purpose binding. Ask the cell administrator to reconcile the purpose routing.", true,
                        [purpose]);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var changedProfiles = profiles.Where(this.IsWorkspaceEditable)
                .Where(p => selections.Values.Any(s => s.ConnectionId == p.Id) ||
                            p.PurposeBindings.Any(b => Enum.TryParse<AiPurpose>(b.Purpose, out var purpose) && selections.ContainsKey(purpose)))
                .ToList();
            foreach (var profile in changedProfiles)
            {
                this.DetachProfile(profile.Id);
                dbContext.Attach(profile);
                foreach (var binding in profile.PurposeBindings
                             .Where(b => Enum.TryParse<AiPurpose>(b.Purpose, out var purpose) && selections.ContainsKey(purpose)).ToList())
                {
                    profile.PurposeBindings.Remove(binding);
                }

                foreach (var (purpose, selected) in selections.Where(pair => pair.Value.ConnectionId == profile.Id))
                {
                    profile.PurposeBindings.Add(
                        new AiPurposeBindingRecord
                        {
                            Id = Guid.NewGuid(), ConnectionProfileId = profile.Id, ConfiguredModelId = selected.ConfiguredModelId,
                            Purpose = purpose.ToString(),
                            ProtocolMode =
                                purpose == AiPurpose.EmbeddingDefault ? ProviderDeclaredProtocolModes.Embeddings : ProviderDeclaredProtocolModes.Auto,
                            IsEnabled = true, CreatedAt = now, UpdatedAt = now,
                        });
                }

                if (selections.Values.Any(s => s.ConnectionId == profile.Id))
                {
                    profile.IsActive = true;
                }

                profile.UpdatedAt = now;
            }

            await dbContext.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            foreach (var profile in changedProfiles)
            {
                await this.AuditAsync("workspace-purposes-selected", profile, false, ct);
            }

            return new(true);
        }
        catch (Exception ex) when (IsConcurrentMutation(ex))
        {
            foreach (var profile in profiles)
            {
                this.DetachProfile(profile.Id);
            }

            return new(false, "AI configuration changed while selecting models. Reload the connections and retry.", true);
        }
    }

    private bool IsWorkspaceEditable(AiConnectionProfileRecord profile)
    {
        var identity = providerDrivers.ResolveIdentity(profile.ProviderKind).KeyOr(profile.ProviderKind);
        var mode = providerDrivers.ResolveAuthMode(identity, profile.AuthMode).ValueOr(profile.AuthMode);
        return identity switch
        {
            "meisterdev/googleVertex" => ProviderVocabulary.ValuesEqual(mode, identity + ":GcpAdc"),
            "meisterdev/openAi" or "meisterdev/anthropic" or "meisterdev/awsBedrock" or
                "meisterdev/openAiCompatible" or "meisterdev/azureOpenAi" => ProviderVocabulary.ValuesEqual(mode, identity + ":ApiKey"),
            "meisterdev/mock" => true,
            _ => false,
        };
    }

    private async Task GuardCandidateReferencesAsync(AiConnectionDto candidate, CancellationToken ct)
    {
        var tenantReferences = await dbContext.LogicalModels.AsNoTracking()
            .Where(r => r.ConnectionId == candidate.Id).ToListAsync(ct);
        var clientReferences = await dbContext.LogicalModelOverrides.AsNoTracking()
            .Where(r => r.ConnectionId == candidate.Id).ToListAsync(ct);
        foreach (var reference in tenantReferences.Cast<ILogicalModelMapping>().Concat(clientReferences))
        {
            LogicalModelCapabilityValidator.ValidateMapping(
                new LogicalModelDto(
                    reference.Id, reference.Name,
                    reference.Capability, reference.ConnectionId, reference.ConfiguredModelId, reference.ReasoningEffort,
                    reference.ProtocolMode), candidate);
        }
    }

    private Task<AiConnectionProfileRecord?> LoadProfileAsync(Guid id, bool tracking, CancellationToken ct)
    {
        var query = dbContext.AiConnectionProfiles.Include(p => p.ConfiguredModels)
            .Include(p => p.PurposeBindings).Include(p => p.VerificationSnapshot).Where(p => p.Id == id);
        return (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(ct);
    }

    private void DetachProfile(Guid id)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AiConnectionProfileRecord p && p.Id == id ||
                entry.Entity is AiConfiguredModelRecord m && m.ConnectionProfileId == id ||
                entry.Entity is AiPurposeBindingRecord b && b.ConnectionProfileId == id ||
                entry.Entity is AiVerificationSnapshotRecord v && v.ConnectionProfileId == id)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static bool IsConcurrentMutation(Exception exception)
    {
        // Npgsql can wrap serialization failures in its execution strategy's InvalidOperationException.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException ||
                current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }

    private static string SnapshotStamp(AiConnectionProfileRecord record)
    {
        // The digest includes credentials and child capabilities even when a writer does not advance UpdatedAt.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                record.Id, record.ClientId, record.TenantId, record.DisplayName, record.ProviderKind, record.AuthMode,
                record.BaseUrl, record.ProtectedSecret, record.DefaultHeaders, record.DefaultQueryParams, record.ProviderSettings,
                record.DiscoveryMode, record.IsActive, record.UpdatedAt,
                Models = record.ConfiguredModels.OrderBy(m => m.Id).Select(m => new
                {
                    m.Id, m.RemoteModelId, m.DisplayName, m.OperationKinds, m.SupportedProtocolModes, m.TokenizerName,
                    m.MaxInputTokens, m.EmbeddingDimensions, m.SupportsStructuredOutput, m.SupportsToolUse,
                    m.MaxContextTokens, m.SupportsReasoning, m.SupportsPromptCaching, m.ReasoningContentField,
                    m.Source, m.LastSeenAt, m.InputCostPer1MUsd, m.OutputCostPer1MUsd, m.CachedInputCostPer1MUsd, m.CacheWriteCostPer1MUsd,
                }),
                Bindings = record.PurposeBindings.OrderBy(b => b.Id).Select(b => new
                    { b.Id, b.Purpose, b.ConfiguredModelId, b.ProtocolMode, b.IsEnabled, b.UpdatedAt }),
                Verification = record.VerificationSnapshot is { } v ? new { v.Status, v.CheckedAt, v.Summary, v.FailureCategory } : null,
            });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
