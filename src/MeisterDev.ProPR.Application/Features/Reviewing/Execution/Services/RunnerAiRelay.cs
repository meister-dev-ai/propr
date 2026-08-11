// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Services;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Performs chat completions for an executor, and is the one place a job's spend can be stopped.
/// </summary>
public sealed class RunnerAiRelay(
    IRunnerCallAuthorizer authorizer,
    IRunnerJobBudgetRegistry budgets,
    IRunnerRelayModelResolver models,
    IRunnerRelayUsageRecorder usage,
    RunnerRelayReplayCache replays) : IRunnerAiRelay
{
    /// <inheritdoc />
    public async Task<RunnerRelayResult> CompleteAsync(
        RunnerCallContext call,
        RunnerRelayRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            return RunnerRelayResult.NotAuthorized(authorization.Refusal);
        }

        var budget = budgets.Find(call.JobId);
        if (budget is null)
        {
            // Without the job's budget there is nothing to charge, and serving an uncharged completion is
            // how a job spends past its cap. Refusing is the safe direction.
            return RunnerRelayResult.JobNotHeld();
        }

        // A retry of a completion already performed is answered from what it produced. The money was spent
        // on the first attempt; charging again would make the cap trip on spend that never happened.
        if (replays.TryGet(call.JobId, request.IdempotencyKey, out var alreadyServed))
        {
            return RunnerRelayResult.Completed(alreadyServed, budget.IsIncrementSoftCapReached(), replayed: true);
        }

        // Checked before the call, not after: refusing to spend is the only enforcement that works, since
        // noticing afterwards means the money is already gone.
        try
        {
            budget.ThrowIfHardCapReached();
        }
        catch (BudgetHardCapReachedException reached)
        {
            return RunnerRelayResult.BudgetExceeded(reached.Breach);
        }

        var model = await models.ResolveAsync(authorization.ClientId, request.LogicalModelName, ct);
        if (model is null)
        {
            throw new InvalidOperationException(
                $"The logical model '{request.LogicalModelName}' named by the job manifest could not be "
                + "resolved to a configured connection.");
        }

        var response = await model.Client.GetResponseAsync(request.Messages, request.Options, ct);

        // Recorded once per physical call and attributed to the logical model, keyed so a replay of the
        // record itself cannot double-count either.
        await usage.RecordAsync(
            call.JobId,
            request.LogicalModelName,
            request.IdempotencyKey,
            response.Usage,
            ct);

        // Priced here, against the resolved binding's rates, with the same extractor and calculator the
        // in-process budget decorator uses. An unpriced call charging nothing is how a capped job used to
        // spend without limit through a runner.
        budget.RecordCall(
            AiCostCalculator.Calculate(
                AiTokenUsageExtractor.FromResponse(response, model.ProviderKind),
                model.Pricing).Usd);

        replays.Store(call.JobId, request.IdempotencyKey, response);

        // The soft cap is reported, never enforced here. It means wind down to a synthesis rather than
        // stop, and synthesis still needs completions to happen.
        return RunnerRelayResult.Completed(response, budget.IsIncrementSoftCapReached());
    }
}

/// <summary>
///     Answers already given, held across requests.
///     <para>
///         The relay itself is scoped to one HTTP request, so this cache is what makes the idempotency key
///         mean anything: a retry after a network failure arrives on a different request, and a cache that
///         died with the first one would charge the retry as a second completion.
///     </para>
/// </summary>
public sealed class RunnerRelayReplayCache
{
    private readonly ConcurrentDictionary<(Guid JobId, string Key), ChatResponse> _served = new();

    /// <summary>Looks up the answer an earlier attempt under this key already produced.</summary>
    public bool TryGet(Guid jobId, string idempotencyKey, out ChatResponse response)
    {
        var found = this._served.TryGetValue((jobId, idempotencyKey), out var served);
        response = served!;
        return found;
    }

    /// <summary>Keeps a served answer so a retry carrying the same key is answered, not re-charged.</summary>
    public void Store(Guid jobId, string idempotencyKey, ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        this._served[(jobId, idempotencyKey)] = response;
    }

    /// <summary>Drops what is held for a job whose lease has ended on this replica.</summary>
    public void Release(Guid jobId)
    {
        foreach (var key in this._served.Keys.Where(key => key.JobId == jobId).ToList())
        {
            this._served.TryRemove(key, out _);
        }
    }
}

/// <summary>
///     Holds each leased job's budget, so a runner's spend is charged against the job rather than against
///     whichever request thread happened to serve it.
/// </summary>
public sealed class RunnerJobBudgetRegistry : IRunnerJobBudgetRegistry
{
    private readonly ConcurrentDictionary<Guid, BudgetScope> _byJob = new();

    /// <inheritdoc />
    public void Register(Guid jobId, BudgetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        this._byJob[jobId] = scope;
    }

    /// <inheritdoc />
    public BudgetScope? Find(Guid jobId)
    {
        return this._byJob.TryGetValue(jobId, out var scope) ? scope : null;
    }

    /// <inheritdoc />
    public void Release(Guid jobId)
    {
        this._byJob.TryRemove(jobId, out _);
    }
}
