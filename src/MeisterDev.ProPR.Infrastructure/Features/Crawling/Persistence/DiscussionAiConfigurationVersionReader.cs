// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence;

public sealed class DiscussionAiConfigurationVersionReader(IDbContextFactory<MeisterProPRDbContext> factory) : IAiConfigurationVersionReader
{
    public async Task<string> ReadAsync(Guid clientId, IReadOnlyList<AiPurpose> requestedPurposes, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tenant = await db.Clients.Where(client => client.Id == clientId).Select(client => client.TenantId).SingleAsync(ct);
        var purposes = requestedPurposes.SelectMany(purpose => new[] { purpose }.Concat(AiPurposeFallbacks.Chain(purpose))).Distinct().ToArray();
        var purposeNames = purposes.Select(purpose => purpose.ToString()).ToArray();
        var purposeRows = await db.ClientPurposeLogicalModels.AsNoTracking()
            .Where(mapping => mapping.ClientId == clientId && purposes.Contains(mapping.Purpose))
            .OrderBy(mapping => mapping.Id).Select(mapping => new { mapping.LogicalModelName, mapping.Purpose, mapping.ReasoningLevel }).ToListAsync(ct);
        var mappings = purposeRows.Select(mapping => PurposeRow(mapping.LogicalModelName, mapping.Purpose, mapping.ReasoningLevel)).ToList();
        var logicalNames = purposeRows.Select(mapping => mapping.LogicalModelName).ToArray();
        var overrides = (await db.LogicalModelOverrides.AsNoTracking().Where(mapping => mapping.ClientId == clientId && logicalNames.Contains(mapping.Name))
                .OrderBy(mapping => mapping.Id).Select(mapping => new
                    { mapping.Name, mapping.ConnectionId, mapping.ConfiguredModelId, mapping.ProtocolMode, mapping.ReasoningLevel }).ToListAsync(ct))
            .Select(mapping => LogicalModelRow(mapping.Name, mapping.ConnectionId, mapping.ConfiguredModelId, mapping.ProtocolMode, mapping.ReasoningLevel))
            .ToList();
        var logicalModels = (await db.LogicalModels.AsNoTracking().Where(mapping => mapping.TenantId == tenant && logicalNames.Contains(mapping.Name))
                .OrderBy(mapping => mapping.Id).Select(mapping => new
                    { mapping.Name, mapping.ConnectionId, mapping.ConfiguredModelId, mapping.ProtocolMode, mapping.ReasoningLevel }).ToListAsync(ct))
            .Select(mapping => LogicalModelRow(mapping.Name, mapping.ConnectionId, mapping.ConfiguredModelId, mapping.ProtocolMode, mapping.ReasoningLevel))
            .ToList();
        var bindings = await db.AiPurposeBindings.AsNoTracking().Where(binding => purposeNames.Contains(binding.Purpose) && binding.ConnectionProfile != null &&
                                                                                  (binding.ConnectionProfile.ClientId == clientId ||
                                                                                   binding.ConnectionProfile.TenantId == tenant))
            .OrderBy(binding => binding.Id).Select(binding => new
            {
                binding.Id,
                binding.ConnectionProfileId,
                binding.ConfiguredModelId,
                binding.ProtocolMode,
                binding.IsEnabled,
                binding.Purpose,
                ProfileActive = binding.ConnectionProfile!.IsActive,
                binding.ConnectionProfile.ProviderKind,
                binding.ConnectionProfile.DisplayName,
                binding.ConnectionProfile.BaseUrl,
                binding.ConnectionProfile.ProviderSettings,
                Model = binding.ConfiguredModel == null ? null : binding.ConfiguredModel.RemoteModelId
            }).ToListAsync(ct);
        var modelIds = overrides.Select(mapping => mapping.ConfiguredModelId).Concat(logicalModels.Select(mapping => mapping.ConfiguredModelId))
            .Concat(bindings.Select(binding => binding.ConfiguredModelId)).Distinct().ToArray();
        var connectionIds = overrides.Select(mapping => mapping.ConnectionId).Concat(logicalModels.Select(mapping => mapping.ConnectionId))
            .Concat(bindings.Select(binding => binding.ConnectionProfileId)).Distinct().ToArray();
        var profiles = await db.AiConnectionProfiles.AsNoTracking().Where(profile => connectionIds.Contains(profile.Id) &&
                                                                                     (profile.ClientId == clientId || profile.TenantId == tenant))
            .OrderBy(profile => profile.Id)
            .Select(profile => new
            {
                profile.Id, profile.ProviderKind, profile.BaseUrl, profile.ProviderSettings, profile.IsActive, profile.AuthMode, profile.DefaultHeaders,
                profile.DefaultQueryParams
            })
            .ToListAsync(ct);
        var models = await db.AiConfiguredModels.AsNoTracking().Where(model => modelIds.Contains(model.Id)).OrderBy(model => model.Id)
            .Select(model => new
            {
                model.Id, model.RemoteModelId, model.OperationKinds, model.SupportedProtocolModes,
                model.TokenizerName, model.MaxInputTokens, model.MaxContextTokens, model.EmbeddingDimensions,
                model.SupportsStructuredOutput, model.SupportsReasoning, model.SupportsToolUse,
                model.SupportsPromptCaching, model.ReasoningContentField,
            })
            .ToListAsync(ct);
        return DiscussionThreadProgress.Fingerprint(
            DiscussionFingerprints.CanonicalValue(new { mappings, overrides, logicalModels, profiles, bindings, models }));
    }

    /// <summary>
    ///     A purpose assignment as the fingerprint states it. The level appears only when the assignment sets one, so
    ///     an assignment without a level keeps the fingerprint it had before purpose assignments carried levels.
    /// </summary>
    internal static object PurposeRow(string logicalModelName, AiPurpose purpose, string? reasoningLevel)
    {
        return reasoningLevel is null
            ? new { LogicalModelName = logicalModelName, Purpose = purpose }
            : new { LogicalModelName = logicalModelName, Purpose = purpose, ReasoningLevel = reasoningLevel };
    }

    /// <summary>A logical model as the fingerprint states it.</summary>
    internal static LogicalModelFingerprintRow LogicalModelRow(
        string name,
        Guid connectionId,
        Guid configuredModelId,
        string protocolMode,
        string? reasoningLevel)
    {
        return new LogicalModelFingerprintRow(name, connectionId, configuredModelId, protocolMode, FingerprintLevel(reasoningLevel));
    }

    /// <summary>
    ///     A level as the fingerprint states it. Before levels had names, the fingerprint held the effort as the number
    ///     of the former enumeration: 0 for no effort, 1 for low, 2 for medium and 3 for high. Those four keep their
    ///     numbers, so a configuration that did not change keeps its fingerprint, and memory and insights are not
    ///     processed again after the upgrade. Every other level appears by its name.
    /// </summary>
    internal static object FingerprintLevel(string? level)
    {
        return level switch
        {
            null => 0,
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            _ => level,
        };
    }

    /// <summary>A logical model row of the fingerprint, with the property names the fingerprint has always used.</summary>
    internal sealed record LogicalModelFingerprintRow(
        string Name,
        Guid ConnectionId,
        Guid ConfiguredModelId,
        string ProtocolMode,
        object ReasoningEffort);
}
