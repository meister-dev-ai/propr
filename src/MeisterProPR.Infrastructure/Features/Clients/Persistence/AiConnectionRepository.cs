// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for provider-neutral AI connection profiles.</summary>
public sealed class AiConnectionRepository(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IAiConnectionRepository
{
    private const string SecretPurpose = "AiConnectionApiKey";

    private static readonly AiPurpose[] RequiredActivationPurposes =
    [
        AiPurpose.ReviewDefault,
        AiPurpose.MemoryReconsideration,
        AiPurpose.EmbeddingDefault,
    ];

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
            ProtectedSecret = this.ProtectSecret(request.Secret),
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

        var updatedModels = BuildConfiguredModels(record.Id, request, record.ConfiguredModels);
        var now = DateTimeOffset.UtcNow;
        var updatedBindings = BuildPurposeBindings(record.Id, request.PurposeBindings, updatedModels, now);
        var shouldInvalidateVerification = this.RequiresVerificationReset(record, request, updatedModels, updatedBindings);

        record.DisplayName = request.DisplayName;
        record.ProviderKind = request.ProviderKind.ToString();
        record.BaseUrl = request.BaseUrl;
        record.AuthMode = request.AuthMode.ToString();
        record.DiscoveryMode = request.DiscoveryMode.ToString();
        record.DefaultHeaders = NormalizeMap(request.DefaultHeaders);
        record.DefaultQueryParams = NormalizeMap(request.DefaultQueryParams);
        record.ProtectedSecret = request.Secret is null ? record.ProtectedSecret : this.ProtectSecret(request.Secret);
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
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnectionProfiles.FindAsync([connectionId], ct);
        if (record is null)
        {
            return false;
        }

        dbContext.AiConnectionProfiles.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateAsync(Guid connectionId, CancellationToken ct = default)
    {
        var target = await dbContext.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .Include(profile => profile.PurposeBindings)
            .Include(profile => profile.VerificationSnapshot)
            .FirstOrDefaultAsync(profile => profile.Id == connectionId, ct);
        if (target is null)
        {
            return false;
        }

        if (!string.Equals(target.VerificationSnapshot?.Status, AiVerificationStatus.Verified.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!HasValidRequiredBindings(target))
        {
            return false;
        }

        var others = await dbContext.AiConnectionProfiles
            .Where(profile => profile.ClientId == target.ClientId && profile.IsActive && profile.Id != connectionId)
            .ToListAsync(ct);

        foreach (var other in others)
        {
            other.IsActive = false;
            other.UpdatedAt = DateTimeOffset.UtcNow;
        }

        target.IsActive = true;
        target.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(ct);
        return true;
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

    private string? ProtectSecret(string? secret)
    {
        return string.IsNullOrWhiteSpace(secret)
            ? secret
            : secretProtectionCodec.Protect(secret, SecretPurpose);
    }

    private string? UnprotectSecret(string? secret)
    {
        return string.IsNullOrWhiteSpace(secret)
            ? secret
            : secretProtectionCodec.Unprotect(secret, SecretPurpose);
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
            this.UnprotectSecret(record.ProtectedSecret));
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
            record.OutputCostPer1MUsd);
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
                var recordId = model.Id != Guid.Empty
                    ? model.Id
                    : existingByRemoteModelId.TryGetValue(model.RemoteModelId, out var existingRecord)
                        ? existingRecord.Id
                        : Guid.NewGuid();

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
                    EmbeddingDimensions = model.EmbeddingDimensions,
                    SupportsStructuredOutput = model.SupportsStructuredOutput,
                    SupportsToolUse = model.SupportsToolUse,
                    Source = model.Source.ToString(),
                    LastSeenAt = model.LastSeenAt,
                    InputCostPer1MUsd = model.InputCostPer1MUsd,
                    OutputCostPer1MUsd = model.OutputCostPer1MUsd,
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
                var modelId = binding.ConfiguredModelId.HasValue && binding.ConfiguredModelId.Value != Guid.Empty
                    ? binding.ConfiguredModelId.Value
                    : binding.RemoteModelId is not null && modelsByRemoteModelId.TryGetValue(binding.RemoteModelId, out var remoteModel)
                        ? remoteModel.Id
                        : Guid.Empty;

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

    private static bool HasValidRequiredBindings(AiConnectionProfileRecord record)
    {
        foreach (var purpose in RequiredActivationPurposes)
        {
            var binding = FindActiveBindingRecord(record, purpose);

            if (binding is null)
            {
                return false;
            }

            var model = record.ConfiguredModels.FirstOrDefault(candidate => candidate.Id == binding.ConfiguredModelId);
            if (model is null || !IsBindingValid(purpose, model, binding))
            {
                return false;
            }
        }

        return true;
    }

    private static AiPurposeBindingRecord? FindActiveBindingRecord(AiConnectionProfileRecord record, AiPurpose purpose)
    {
        var binding = record.PurposeBindings.FirstOrDefault(candidate =>
            string.Equals(candidate.Purpose, purpose.ToString(), StringComparison.Ordinal) && candidate.IsEnabled);

        if (binding is not null || !IsReviewEffortOverride(purpose))
        {
            return binding;
        }

        return record.PurposeBindings.FirstOrDefault(candidate =>
            string.Equals(candidate.Purpose, AiPurpose.ReviewDefault.ToString(), StringComparison.Ordinal) && candidate.IsEnabled);
    }

    private static bool IsReviewEffortOverride(AiPurpose purpose)
    {
        return purpose is AiPurpose.ReviewLowEffort or AiPurpose.ReviewMediumEffort or AiPurpose.ReviewHighEffort;
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
        var existingSecret = this.UnprotectSecret(record.ProtectedSecret);
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
                existing.EmbeddingDimensions != model.EmbeddingDimensions ||
                existing.SupportsStructuredOutput != model.SupportsStructuredOutput ||
                existing.SupportsToolUse != model.SupportsToolUse ||
                !string.Equals(existing.Source, model.Source, StringComparison.Ordinal) ||
                existing.LastSeenAt != model.LastSeenAt ||
                existing.InputCostPer1MUsd != model.InputCostPer1MUsd ||
                existing.OutputCostPer1MUsd != model.OutputCostPer1MUsd)
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

    public Task<bool> ActivateAsync(Guid connectionId, string model, CancellationToken ct = default)
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
                    ?.OutputCostPer1MUsd))
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
