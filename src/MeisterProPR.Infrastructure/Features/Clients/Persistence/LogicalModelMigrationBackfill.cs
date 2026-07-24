// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class LogicalModelMigrationBackfill(
    MeisterProPRDbContext db,
    IAiConnectionRepository connections,
    ILogicalModelCatalogRepository catalog,
    ILogger<LogicalModelMigrationBackfill>? logger = null) : ILogicalModelMigrationBackfill
{
    private readonly ILogger _logger = logger ?? NullLogger<LogicalModelMigrationBackfill>.Instance;

    /// <inheritdoc />
    public async Task<int> BackfillAllAsync(CancellationToken ct)
    {
        var clientIds = await db.Clients.AsNoTracking().Select(c => c.Id).ToListAsync(ct);
        var total = 0;
        foreach (var clientId in clientIds)
        {
            total += await this.BackfillClientReviewPassesAsync(clientId, ct);
            total += await this.BackfillClientPurposesAsync(clientId, ct);
        }

        return total;
    }

    /// <inheritdoc />
    public async Task<int> BackfillClientReviewPassesAsync(Guid clientId, CancellationToken ct)
    {
        // Legacy passes = a concrete configured model, no logical-model name yet. Already-named passes are skipped,
        // which makes the whole backfill idempotent.
        var legacyPasses = await db.ClientReviewPasses
            .Where(pass => pass.ClientId == clientId && pass.ConfiguredModelId != null && pass.LogicalModelName == null)
            .OrderBy(pass => pass.Ordinal)
            .ToListAsync(ct);
        if (legacyPasses.Count == 0)
        {
            return 0;
        }

        var overrides = (await catalog.GetClientOverridesAsync(clientId, ct)).ToList();
        var takenNames = overrides.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        var migrated = 0;
        foreach (var pass in legacyPasses)
        {
            var binding = await connections.GetModelBindingAsync(clientId, pass.ConfiguredModelId!.Value, ct);
            if (binding is null)
            {
                // The configured model no longer resolves for this client; leave the pass untouched rather than mint a
                // role that cannot resolve. The pass keeps working via its (still-present) configured-model id.
                this._logger.LogWarning(
                    "Logical-model backfill: client {ClientId} pass {Ordinal} configured model {ModelId} no longer resolves; skipped.",
                    clientId,
                    pass.Ordinal,
                    pass.ConfiguredModelId);
                continue;
            }

            var effort = pass.ReasoningEffort ?? ReviewReasoningEffort.None;

            var name = await this.FindOrCreateOverrideAsync(clientId, overrides, takenNames, AiOperationKind.Chat, binding, effort, ct);
            if (name is null)
            {
                // The configured model cannot serve chat — should not happen for a chat pass, but never let one bad
                // row abort the whole backfill.
                this._logger.LogWarning(
                    "Logical-model backfill: client {ClientId} pass {Ordinal} could not synthesize a role; skipped.",
                    clientId,
                    pass.Ordinal);
                continue;
            }

            pass.LogicalModelName = name;
            migrated++;
        }

        await db.SaveChangesAsync(ct);
        return migrated;
    }

    /// <inheritdoc />
    public async Task<int> BackfillClientPurposesAsync(Guid clientId, CancellationToken ct)
    {
        // Purposes already mapped to a logical model are skipped (idempotent). Every other purpose that still has an
        // active connection binding is migrated: synthesize/reuse a per-client logical model matching the binding, then
        // map the purpose to it — so the purpose no longer depends on the (editor-less) legacy purpose-binding fallback.
        var alreadyMapped = await catalog.GetPurposeRolesAsync(clientId, ct);

        var overrides = (await catalog.GetClientOverridesAsync(clientId, ct)).ToList();
        var takenNames = overrides.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        var migrated = 0;
        foreach (var purpose in Enum.GetValues<AiPurpose>())
        {
            if (alreadyMapped.ContainsKey(purpose))
            {
                continue;
            }

            var binding = await connections.GetActiveBindingForPurposeAsync(clientId, purpose, ct);
            if (binding is null)
            {
                // The purpose was never bound to a model for this client; nothing to migrate (it would have thrown at
                // runtime under the legacy path too).
                continue;
            }

            var capability = purpose == AiPurpose.EmbeddingDefault ? AiOperationKind.Embedding : AiOperationKind.Chat;

            // Don't mint a role the resolver would reject: the bound model must actually serve the purpose's capability.
            var capable = capability == AiOperationKind.Embedding ? binding.Model.SupportsEmbedding : binding.Model.SupportsChat;
            if (!capable)
            {
                this._logger.LogWarning(
                    "Logical-model backfill: client {ClientId} purpose {Purpose} binds model {ModelId} which cannot serve {Capability}; skipped.",
                    clientId,
                    purpose,
                    binding.Model.Id,
                    capability);
                continue;
            }

            // Purpose bindings carry no reasoning effort, so the faithful translation is None: the resolver applies the
            // logical model's effort, and None preserves the legacy binding's behavior exactly.
            var name = await this.FindOrCreateOverrideAsync(clientId, overrides, takenNames, capability, binding, ReviewReasoningEffort.None, ct);
            if (name is null)
            {
                this._logger.LogWarning(
                    "Logical-model backfill: client {ClientId} purpose {Purpose} could not synthesize a role; skipped.",
                    clientId,
                    purpose);
                continue;
            }

            await catalog.SetPurposeRoleAsync(clientId, purpose, name, ct);
            migrated++;
        }

        return migrated;
    }

    // Returns the name of a per-client override matching the (capability, connection, model, effort, protocol) tuple —
    // reusing an existing override so parity holds exactly, otherwise minting a uniquely-named one. Returns null when
    // the mapping is invalid (the model cannot serve the capability), so callers can skip that row.
    private async Task<string?> FindOrCreateOverrideAsync(
        Guid clientId,
        List<LogicalModelDto> overrides,
        HashSet<string> takenNames,
        AiOperationKind capability,
        AiResolvedPurposeBindingDto binding,
        ReviewReasoningEffort effort,
        CancellationToken ct)
    {
        var protocol = binding.Binding.ProtocolMode;

        var match = overrides.FirstOrDefault(entry =>
            entry.Capability == capability
            && entry.ConnectionId == binding.Connection.Id
            && entry.ConfiguredModelId == binding.Model.Id
            && entry.ReasoningEffort == effort
            && entry.ProtocolMode == protocol);
        if (match is not null)
        {
            return match.Name;
        }

        var name = GenerateUniqueName(BuildRoleName(binding.Model.RemoteModelId, effort), takenNames);
        var dto = new LogicalModelDto(
            Guid.NewGuid(),
            name,
            capability,
            binding.Connection.Id,
            binding.Model.Id,
            effort,
            protocol);
        try
        {
            await catalog.AddClientOverrideAsync(clientId, dto, ct);
        }
        catch (LogicalModelReferenceInvalidException)
        {
            return null;
        }

        overrides.Add(dto);
        takenNames.Add(name);
        return name;
    }

    // Deterministic, human-readable role name; identical (model, effort) tuples collapse to one role.
    private static string BuildRoleName(string remoteModelId, ReviewReasoningEffort effort)
    {
        var suffix = effort == ReviewReasoningEffort.None ? string.Empty : $"-{effort.ToString().ToLowerInvariant()}";
        var name = $"migrated-{remoteModelId}{suffix}";
        return name.Length <= 100 ? name : name[..100];
    }

    // Generates a name unique within the client's overrides (disambiguating a same-model/effort name that already
    // maps to a different connection/protocol tuple).
    private static string GenerateUniqueName(string preferred, HashSet<string> taken)
    {
        if (!taken.Contains(preferred))
        {
            return preferred;
        }

        for (var suffix = 2;; suffix++)
        {
            var candidate = $"{preferred}-{suffix}";
            if (candidate.Length > 100)
            {
                candidate = candidate[..100];
            }

            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
