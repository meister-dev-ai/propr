// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;

/// <summary>
///     A chat client that performs nothing itself and asks the control plane to do it.
///     <para>
///         Presented to the pipeline as an ordinary <see cref="IChatClient" />, because that is the whole
///         point: the review code does not branch on where it is running. What changes underneath is that
///         no provider key is present, the model is chosen by name, and the spend is charged centrally
///         before the call rather than counted afterwards.
///     </para>
///     <para>
///         It sits with the pipeline and not with the relay it calls, because the reasoning request is
///         expressed differently by each provider family and the shaping that expresses it lives here.
///     </para>
///     <para>
///         This is the in-process binding: it calls <see cref="IRunnerAiRelay" /> directly. A runner uses the
///         type of the same name in the Runner project, which reaches the relay over HTTP. This one is
///         exercised by the transcript-equivalence test, which drives both bindings through one pass and
///         compares what each sends.
///     </para>
/// </summary>
/// <param name="call">The job and lease generation every relayed call is authorized against.</param>
/// <param name="logicalModelName">The named model role the control plane resolves to a connection.</param>
/// <param name="relay">The control-plane relay that performs the completion.</param>
/// <param name="reasoningEffort">
///     How hard the model is asked to reason on this job. Stated separately from the options the pass
///     builds, because the reasoning request is rebuilt for every completion out of the job's
///     reasoning-capture decision, and the effort has to survive that rebuild.
/// </param>
/// <param name="idempotencyKeyFactory">Names each attempt, so a retry is answered instead of recharged.</param>
/// <param name="logger">Reports a provider request dropped because the tenant keeps no reasoning.</param>
public sealed partial class RelayChatClient(
    RunnerCallContext call,
    string logicalModelName,
    IRunnerAiRelay relay,
    ReviewReasoningEffort reasoningEffort,
    Func<string>? idempotencyKeyFactory = null,
    ILogger<RelayChatClient>? logger = null) : IChatClient
{
    private readonly Func<string> _newKey = idempotencyKeyFactory ?? (() => Guid.NewGuid().ToString("N"));

    private readonly ILogger _log = logger ?? NullLogger<RelayChatClient>.Instance;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var result = await relay.CompleteAsync(
            call,
            new RunnerRelayRequest(
                logicalModelName,
                [.. messages],
                capturesReasoning => ApplyReasoningCapture(options, capturesReasoning),
                this._newKey()),
            cancellationToken);

        if (result.IsCompleted)
        {
            return result.Response!;
        }

        // A hard cap surfaces as the condition the pipeline already handles, so a job stopped by budget
        // finalises as budget-exceeded and not as a generic failure. The refusal settles that on its own: the
        // cap detail rides along when the relay reported one, and a refusal that carries none is still a cap.
        if (result.Refusal == RunnerRelayRefusal.BudgetHardCapReached)
        {
            throw new BudgetHardCapReachedException(result.Breach);
        }

        // A job the control plane is no longer holding open carries no authorization reason, so the refusal
        // this exception reports is stated here. Without it the message reads "None", which names nothing the
        // reader can act on. The HTTP binding answers the same condition with the lease refusal.
        throw new RunnerCallRefusedException(
            nameof(this.GetResponseAsync),
            result.Refusal == RunnerRelayRefusal.JobNotHeld ? RunnerCallRefusal.JobNotExecuting : result.CallRefusal);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Streaming is answered from the completed response rather than streamed through the relay. The
    ///     review pipeline consumes whole responses; presenting a fake stream keeps the interface honest
    ///     without pretending to a token-by-token path that nothing here uses.
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var response = await this.GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents);
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing owned: the provider client lives on the control plane, which is the point.
    }

    // The pass shaped these options from the decision it read when the review started, and the relay reads
    // the tenant's answer again for every completion. The reasoning request is therefore discarded and stated
    // again from the current answer: a tenant that has since forbidden reasoning must not have a summary
    // asked for on its behalf. The configured effort is applied either way, because how hard the model
    // reasons is a separate setting from whether the reasoning text is kept.
    //
    // Every shaping here is applied to a copy. The caller owns the instance it passed and may send it again,
    // to this client or to another, and may be sending it concurrently; a factory cleared or a request
    // written on that instance would reach calls this decision does not govern.
    private ChatOptions? ApplyReasoningCapture(ChatOptions? options, bool capturesReasoning)
    {
        var asksForSomething = capturesReasoning || reasoningEffort != ReviewReasoningEffort.None;

        // Nothing to ask for and no reasoning request of this pipeline's to replace: the call reaches the
        // provider shaped as the pass shaped it, apart from a provider request this side cannot read.
        if (!asksForSomething && options?.CarriesAReasoningRequest() != true)
        {
            if (options?.RawRepresentationFactory is null)
            {
                return options;
            }

            // A provider request the pipeline did not shape is readable only by running the caller's code
            // against a client it was written for, and this holds a relay client. What it asks the
            // provider for is therefore unknown, the tenant keeps no reasoning, and a request that may
            // ask for some is dropped so the call goes without it.
            LogUnreadableProviderRequestDropped(this._log, call.JobId, logicalModelName);
            var withoutProviderRequest = options.Clone();
            withoutProviderRequest.RawRepresentationFactory = null;
            return withoutProviderRequest;
        }

        // The options argument is nullable by contract, and a call that sent none is governed by the tenant's
        // answer like any other, so the request is built for it here. A provider request the pipeline did not
        // shape is replaced, because the resolved answer is what reaches the provider.
        return (options?.Clone() ?? new ChatOptions()).ApplyResolvedReasoning(capturesReasoning, reasoningEffort);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dropped the provider request on a relayed call for job {JobId} on model {LogicalModelName}: "
                  + "the tenant keeps no model reasoning, and a request the review pipeline did not shape cannot be "
                  + "read without running it.")]
    private static partial void LogUnreadableProviderRequestDropped(
        ILogger logger,
        Guid jobId,
        string logicalModelName);
}
