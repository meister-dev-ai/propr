// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed repository for per-client AI connection configurations.</summary>
public sealed class AiConnectionRepository(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IAiConnectionRepository
{
    private const string ApiKeyPurpose = "AiConnectionApiKey";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiConnectionDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var records = await this.WithReadDbAsync(
            db => db.AiConnections
                .Include(a => a.ModelCapabilities)
                .Where(a => a.ClientId == clientId)
                .OrderByDescending(a => a.CreatedAt)
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
        // Only the default (no ModelCategory) connection participates in the "one active" rule.
        var record = await this.WithReadDbAsync(
            db => db.AiConnections
                .Include(a => a.ModelCapabilities)
                .Where(a => a.ClientId == clientId && a.IsActive && a.ModelCategory == null)
                .FirstOrDefaultAsync(ct),
            ct);
        return record is null ? null : this.ToDto(record);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await this.WithReadDbAsync(
            db => db.AiConnections
                .Include(a => a.ModelCapabilities)
                .FirstOrDefaultAsync(a => a.Id == connectionId, ct),
            ct);
        return record is null ? null : this.ToDto(record);
    }

    /// <inheritdoc />
    public async Task<AiConnectionDto> AddAsync(
        Guid clientId,
        string displayName,
        string endpointUrl,
        IReadOnlyList<string> models,
        string? apiKey,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities = null,
        AiConnectionModelCategory? modelCategory = null,
        CancellationToken ct = default)
    {
        var connectionId = Guid.NewGuid();
        var record = new AiConnectionRecord
        {
            Id = connectionId,
            ClientId = clientId,
            DisplayName = displayName,
            EndpointUrl = endpointUrl,
            Models = models.ToArray(),
            IsActive = false,
            ApiKey = this.ProtectApiKey(apiKey),
            ModelCategory = modelCategory.HasValue ? (short)modelCategory.Value : null,
            ModelCapabilities = ToCapabilityRecords(connectionId, modelCapabilities),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.AiConnections.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return this.ToDto(record);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid connectionId,
        string? displayName,
        string? endpointUrl,
        IReadOnlyList<string>? models,
        string? apiKey,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities,
        CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections
            .Include(a => a.ModelCapabilities)
            .FirstOrDefaultAsync(a => a.Id == connectionId, ct);
        if (record is null)
        {
            return false;
        }

        if (displayName is not null)
        {
            record.DisplayName = displayName;
        }

        if (endpointUrl is not null)
        {
            record.EndpointUrl = endpointUrl;
        }

        if (models is not null)
        {
            record.Models = models.ToArray();
        }

        if (apiKey is not null)
        {
            record.ApiKey = this.ProtectApiKey(apiKey);
        }

        if (modelCapabilities is not null)
        {
            ReplaceModelCapabilities(record, modelCapabilities);
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections.FindAsync([connectionId], ct);
        if (record is null)
        {
            return false;
        }

        dbContext.AiConnections.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateAsync(Guid connectionId, string model, CancellationToken ct = default)
    {
        var target = await dbContext.AiConnections
            .Include(a => a.ModelCapabilities)
            .FirstOrDefaultAsync(a => a.Id == connectionId, ct);
        if (target is null)
        {
            return false;
        }

        if (!target.Models.Contains(model))
        {
            return false;
        }

        if (target.ModelCategory == (short)AiConnectionModelCategory.Embedding &&
            !target.ModelCapabilities.Any(capability =>
                string.Equals(capability.ModelName, model, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (target.ModelCategory.HasValue)
        {
            // Categorized connections (lowEffort, highEffort, embedding, etc.) never set
            // is_active — the partial unique index "ix_ai_connections_client_id_active"
            // only allows one is_active=true per client.  For categorized connections the
            // selected deployment is stored in active_model only; GetForTierAsync uses
            // model_category, not is_active, so this is sufficient.
            target.ActiveModel = model;
        }
        else
        {
            // Default connections: deactivate every other default connection for this client,
            // then mark this one active.
            var others = await dbContext.AiConnections
                .Where(a => a.ClientId == target.ClientId && a.IsActive
                                                          && a.Id != connectionId && a.ModelCategory == null)
                .ToListAsync(ct);

            foreach (var other in others)
            {
                other.IsActive = false;
                other.ActiveModel = null;
            }

            target.IsActive = true;
            target.ActiveModel = model;
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deactivates the specified connection. Returns false if not found.</summary>
    public async Task<bool> DeactivateAsync(Guid connectionId, CancellationToken ct = default)
    {
        var record = await dbContext.AiConnections.FindAsync([connectionId], ct);
        if (record is null)
        {
            return false;
        }

        if (record.ModelCategory.HasValue)
        {
            // Categorized connections: clear selected model only — is_active stays false.
            record.ActiveModel = null;
        }
        else
        {
            record.IsActive = false;
            record.ActiveModel = null;
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Uses a short-lived <see cref="MeisterProPRDbContext" /> from the factory so concurrent
    ///     calls from parallel file-review tasks cannot share the same context instance.
    /// </remarks>
    public async Task<AiConnectionDto?> GetForTierAsync(
        Guid clientId,
        AiConnectionModelCategory tier,
        CancellationToken ct = default)
    {
        var category = (short)tier;
        if (contextFactory is not null)
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var record = await db.AiConnections
                .Include(a => a.ModelCapabilities)
                .AsNoTracking()
                .Where(a => a.ClientId == clientId && a.ModelCategory == category)
                .FirstOrDefaultAsync(ct);
            return record is null ? null : this.ToDto(record);
        }
        else
        {
            var record = await dbContext.AiConnections
                .Include(a => a.ModelCapabilities)
                .AsNoTracking()
                .Where(a => a.ClientId == clientId && a.ModelCategory == category)
                .FirstOrDefaultAsync(ct);
            return record is null ? null : this.ToDto(record);
        }
    }

    private static IReadOnlyList<AiConnectionModelCapabilityDto> MapModelCapabilities(IEnumerable<AiConnectionModelCapabilityRecord> capabilities)
    {
        return capabilities
            .OrderBy(capability => capability.ModelName, StringComparer.OrdinalIgnoreCase)
            .Select(capability => new AiConnectionModelCapabilityDto(
                capability.ModelName,
                capability.TokenizerName,
                capability.MaxInputTokens,
                capability.EmbeddingDimensions,
                capability.InputCostPer1MUsd,
                capability.OutputCostPer1MUsd))
            .ToList()
            .AsReadOnly();
    }

    private string? ProtectApiKey(string? apiKey)
    {
        return string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : secretProtectionCodec.Protect(apiKey, ApiKeyPurpose);
    }

    private string? UnprotectApiKey(string? apiKey)
    {
        return string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : secretProtectionCodec.Unprotect(apiKey, ApiKeyPurpose);
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

    private AiConnectionDto ToDto(AiConnectionRecord r)
    {
        return new AiConnectionDto(
            r.Id,
            r.ClientId,
            r.DisplayName,
            r.EndpointUrl,
            r.Models,
            r.IsActive,
            r.ActiveModel,
            r.CreatedAt,
            r.ModelCategory.HasValue ? (AiConnectionModelCategory)r.ModelCategory.Value : null,
            MapModelCapabilities(r.ModelCapabilities),
            this.UnprotectApiKey(r.ApiKey));
    }

    private static List<AiConnectionModelCapabilityRecord> ToCapabilityRecords(
        Guid connectionId,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities)
    {
        if (modelCapabilities is null || modelCapabilities.Count == 0)
        {
            return [];
        }

        return modelCapabilities
            .Select(capability => new AiConnectionModelCapabilityRecord
            {
                Id = Guid.NewGuid(),
                AiConnectionId = connectionId,
                ModelName = capability.ModelName.Trim(),
                TokenizerName = capability.TokenizerName.Trim(),
                MaxInputTokens = capability.MaxInputTokens,
                EmbeddingDimensions = capability.EmbeddingDimensions,
                InputCostPer1MUsd = capability.InputCostPer1MUsd,
                OutputCostPer1MUsd = capability.OutputCostPer1MUsd,
            })
            .ToList();
    }

    private static void ReplaceModelCapabilities(
        AiConnectionRecord record,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities)
    {
        record.ModelCapabilities.Clear();

        foreach (var capability in ToCapabilityRecords(record.Id, modelCapabilities))
        {
            record.ModelCapabilities.Add(capability);
        }
    }
}
