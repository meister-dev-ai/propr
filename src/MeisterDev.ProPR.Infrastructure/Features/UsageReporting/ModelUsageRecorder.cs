// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Services;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageReporting;

/// <summary>
///     Writes the token spend of one model call to the client's daily usage sample, priced from the model that
///     answered.
/// </summary>
/// <remarks>
///     <para>
///         The prices come off the resolved model rather than through the pricing resolver: this is the model that
///         answered, so its own configured prices are the exact ones, and reading them costs no query on a path
///         that runs once per classified finding.
///     </para>
///     <para>
///         The row is keyed by client, model, logical model, provider, and date, exactly as the review path keys
///         it. Post-hoc spend therefore lands beside review spend rather than in a parallel table, and shows up on
///         its own line wherever usage is sliced by model, because these purposes normally resolve a different
///         model from the review passes.
///     </para>
///     <para>
///         Runs on a fresh context from the factory because this is a best-effort side-write on a path whose
///         caller is holding a request-scoped context it still needs.
///     </para>
/// </remarks>
internal sealed partial class ModelUsageRecorder(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ILogger<ModelUsageRecorder> logger) : IModelUsageRecorder
{
    /// <inheritdoc />
    public async Task RecordAsync(
        Guid clientId,
        IResolvedAiChatRuntime runtime,
        ChatResponse? response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var providerKind = runtime.Connection.ProviderKind;
        var usage = AiTokenUsageExtractor.FromResponse(response, providerKind);

        // A response with no usage payload extracts as all-zero. Recording it would add a row that says a call
        // cost nothing, which is a stronger claim than "the provider did not say".
        if (usage.InputTokens <= 0
            && usage.OutputTokens <= 0
            && usage.CachedInputTokens <= 0
            && usage.CacheWriteTokens <= 0
            && usage.ReasoningTokens <= 0)
        {
            return;
        }

        try
        {
            var model = runtime.Model;
            var cost = AiCostCalculator.Calculate(
                    usage,
                    new ModelPricing(
                        model.InputCostPer1MUsd,
                        model.OutputCostPer1MUsd,
                        model.CachedInputCostPer1MUsd,
                        model.CacheWriteCostPer1MUsd))
                .Usd;

            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await new ClientTokenUsageRepository(db).UpsertAsync(
                clientId,
                model.RemoteModelId,
                DateOnly.FromDateTime(DateTime.UtcNow),
                usage.InputTokens,
                usage.OutputTokens,
                ct,
                usage.CachedInputTokens,
                usage.CacheWriteTokens,
                usage.ReasoningTokens,
                cost,
                runtime.LogicalModelName ?? string.Empty,
                providerKind.ToString()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The tokens are spent either way. Failing the classification that spent them would trade a wrong
            // number for lost work.
            LogRecordingFailed(logger, clientId, model: runtime.Model.RemoteModelId, ex);
        }
    }
}
