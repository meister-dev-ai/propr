// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using System.Reflection;
using MeisterDev.Ai.Providers.Usage;
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
    RunnerRelayReplayCache replays,
    IRunnerJobReasoningCapturePolicy reasoningCapture) : IRunnerAiRelay
{
    // object.MemberwiseClone, bound once. It is protected, so reflection is what reaches it; the binding
    // happens at type load and every call after that is an ordinary delegate invocation.
    private static readonly Func<object, object> ShallowCopy =
        typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<object, object>>();

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

        // Read here and not before the lease check: the decision belongs to the tenant that owns the job, so
        // a caller naming a job it does not hold must not make the control plane read that tenant's policy.
        // Read per completion, so a policy saved while a runner job is in flight governs the rest of it, and
        // read before a replay is served, so a retry is answered under the policy in force now.
        var capturesReasoning = await reasoningCapture.CapturesReasoningAsync(call.JobId, ct);

        // A retry of a completion already performed is answered from what it produced. The money was spent
        // on the first attempt; charging again would make the cap trip on spend that never happened.
        if (replays.TryGet(call.JobId, request.IdempotencyKey, out var alreadyServed))
        {
            // The cached answer was produced under the policy of its own attempt. A tenant that has since
            // forbidden reasoning gets the same completion with the reasoning taken out, so a retry cannot
            // hand back text the tenant no longer permits.
            var answer = capturesReasoning ? alreadyServed : WithoutReasoning(alreadyServed);

            return RunnerRelayResult.Completed(
                answer,
                budget.IsIncrementSoftCapReached(),
                ProviderTokenUsage.FromUsageDetails(answer.Usage),
                replayed: true);
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

        var options = request.OptionsFor(capturesReasoning);

        var response = await model.Client.GetResponseAsync(request.Messages, options, ct);

        // The counters the family's driver mapped, which the runtime pipeline has already put on the response.
        // This side prices them and the executor is handed them, so both sides meter one set of numbers.
        var counters = ProviderTokenUsage.FromUsageDetails(response.Usage);

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
                AiTokenUsageExtractor.FromResponse(response),
                model.Pricing).Usd);

        // Redacted before it is returned, and not only when a retry replays it: a provider returns reasoning
        // parts of its own accord, so the option the request carried does not settle whether the answer holds
        // any. The counters above are read from the completion as it came back, because the spend happened
        // whatever the tenant keeps of the text.
        var served = capturesReasoning ? response : WithoutReasoning(response);

        // Held for a retry as the provider returned it, and redacted on the way out under the policy in force
        // when the retry arrives. Holding the redacted answer would make one restrictive attempt permanent: a
        // tenant that permits reasoning again would be handed a completion stripped of text it had paid for,
        // and there is no second call to produce that text again.
        replays.Store(call.JobId, request.IdempotencyKey, response);

        // The soft cap is reported, never enforced here. It means wind down to a synthesis instead of
        // stopping, and synthesis still needs completions to happen.
        return RunnerRelayResult.Completed(served, budget.IsIncrementSoftCapReached(), counters);
    }

    /// <summary>The same completion with the model's reasoning taken out.</summary>
    /// <remarks>
    ///     Every reasoning part goes, and so does the provider metadata beside it: the properties a provider
    ///     adds to the response and to a turn, and the raw payload every part was read from, because a
    ///     signature or a thinking block left there carries the text this removes. Absence of a reasoning part
    ///     is therefore no reason to skip the work. The properties on a tool call and its result are kept, so
    ///     the tool exchange the next turn continues stays intact. The counters stay, because budgets are
    ///     computed from them and the spend happened.
    /// </remarks>
    private static ChatResponse WithoutReasoning(ChatResponse response)
    {
        var messages = response.Messages.Select(MessageWithoutReasoning).ToList();

        return new ChatResponse(messages)
        {
            ResponseId = response.ResponseId,
            ConversationId = response.ConversationId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            ContinuationToken = response.ContinuationToken,
            Usage = response.Usage,
        };
    }

    /// <summary>One turn of the completion, without its reasoning and without the provider metadata.</summary>
    /// <remarks>
    ///     Cloned and then rewritten, so every member the turn carries survives the redaction. Rebuilding the
    ///     turn by construction dropped the members the constructor does not take, which cost the timestamp.
    ///     The turn's own provider slots are cleared here for the same reason the parts' are: the payload the
    ///     turn was read from and the properties a provider attached to it can carry the reasoning this
    ///     removes.
    /// </remarks>
    private static ChatMessage MessageWithoutReasoning(ChatMessage message)
    {
        var redacted = message.Clone();
        redacted.Contents = message.Contents
            .Where(content => content is not TextReasoningContent)
            .Select(WithoutProviderMetadata)
            .ToList();
        redacted.RawRepresentation = null;
        redacted.AdditionalProperties = null;

        return redacted;
    }

    /// <summary>A copy of one part of a turn, without what the provider sent that part in.</summary>
    /// <remarks>
    ///     A copy, because the completion these parts belong to is held for a retry and the retry is answered
    ///     under the policy in force when it arrives. Clearing the fields on the part itself would clear them
    ///     in the held completion too, so one attempt served under a restrictive policy would strip the answer
    ///     for good and a concurrent retry could observe a half-redacted one. The copy is a shallow clone: the
    ///     abstraction defines two dozen kinds of content and gains more with each release, so rebuilding a
    ///     part by construction would need a case per kind and would drop a kind this build does not know.
    ///     A shallow clone is enough for what this removes.
    ///     <para>
    ///         The payload the part was read from is cleared on every kind. The properties the provider
    ///         attached are cleared on every kind but a tool call and its result, where a provider stores the
    ///         state the next turn of the same tool exchange has to carry back: the Google Vertex driver keeps
    ///         the signed call part there, and Gemini refuses a continuation that arrives without it. Clearing
    ///         them there stopped tool calling for a tenant that keeps no reasoning. The arguments and results
    ///         themselves are what the pipeline built and hold no model reasoning.
    ///     </para>
    /// </remarks>
    private static AIContent WithoutProviderMetadata(AIContent content)
    {
        var copy = (AIContent)ShallowCopy(content);
        copy.RawRepresentation = null;

        if (content is not FunctionCallContent and not FunctionResultContent)
        {
            copy.AdditionalProperties = null;
        }

        return copy;
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
