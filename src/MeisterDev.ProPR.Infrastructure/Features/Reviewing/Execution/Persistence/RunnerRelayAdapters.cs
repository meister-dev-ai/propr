// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Turns the logical model name a runner asked for into a chat client, on the side that holds the
///     credential. This is the step that keeps the provider key off the executor: only a name travels.
/// </summary>
public sealed partial class RunnerRelayModelResolver(
    ILogicalModelResolver logicalModels,
    ILogger<RunnerRelayModelResolver> logger) : IRunnerRelayModelResolver
{
    /// <inheritdoc />
    public async Task<RunnerRelayModel?> ResolveAsync(
        Guid clientId,
        string logicalModelName,
        CancellationToken ct = default)
    {
        try
        {
            var resolved = await logicalModels.ResolveChatRuntimeAsync(clientId, logicalModelName, ct: ct);
            var runtime = resolved.Runtime;

            // The rates travel with the client because the relay is the one place a remote review's spend
            // is charged; a client without its pricing would charge nothing per call.
            return new RunnerRelayModel(
                runtime.ChatClient,
                runtime.Connection.ProviderKind,
                new ModelPricing(
                    runtime.Model.InputCostPer1MUsd,
                    runtime.Model.OutputCostPer1MUsd,
                    runtime.Model.CachedInputCostPer1MUsd,
                    runtime.Model.CacheWriteCostPer1MUsd));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Null rather than a throw: the relay turns this into a refusal the executor understands. The
            // name came from a manifest resolved when the job was dispatched, so a binding that has since
            // been deleted is a configuration change, not a bug in the caller.
            LogResolutionFailed(logger, logicalModelName, clientId, ex);
            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not resolve logical model '{LogicalModelName}' for client {ClientId} on behalf of a runner")]
    private static partial void LogResolutionFailed(
        ILogger logger,
        string logicalModelName,
        Guid clientId,
        Exception ex);
}

/// <summary>
///     Counts what a relayed completion consumed, once per physical call.
///     <para>
///         Deliberately does not write token totals itself. In-process passes accrue their tokens through
///         the protocol recorder when a pass completes, and a runner's spend reaches the same place the same
///         way: the executor ships spend records and the ingest path writes them through that recorder. A
///         second write path here would double-count against the one the pricing pass already reads.
///     </para>
///     <para>
///         What it does own is the idempotency: a retried record must count once, so a batch replayed after
///         a network failure cannot inflate a job's usage.
///     </para>
/// </summary>
public sealed class RunnerRelayUsageRecorder : IRunnerRelayUsageRecorder
{
    private readonly ConcurrentDictionary<(Guid JobId, string Key), long> _counted = new();

    /// <inheritdoc />
    public Task RecordAsync(
        Guid jobId,
        string logicalModelName,
        string idempotencyKey,
        UsageDetails? usage,
        CancellationToken ct = default)
    {
        var tokens = (usage?.InputTokenCount ?? 0) + (usage?.OutputTokenCount ?? 0);
        this._counted.TryAdd((jobId, idempotencyKey), tokens);
        return Task.CompletedTask;
    }

    /// <summary>Tokens counted for a job so far, for reconciling against what ingest later persisted.</summary>
    public long CountedTokens(Guid jobId)
    {
        return this._counted.Where(entry => entry.Key.JobId == jobId).Sum(entry => entry.Value);
    }
}
