// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     A chat client that performs nothing itself and asks the control plane to do it.
///     <para>
///         Presented to the pipeline as an ordinary <see cref="IChatClient" />, because that is the whole
///         point: the review code does not branch on where it is running. What changes underneath is that
///         no provider key is present, the model is chosen by name, and the spend is charged centrally
///         before the call rather than counted afterwards.
///     </para>
/// </summary>
public sealed class RelayChatClient(
    RunnerCallContext call,
    string logicalModelName,
    IRunnerAiRelay relay,
    Func<string>? idempotencyKeyFactory = null) : IChatClient
{
    private readonly Func<string> _newKey = idempotencyKeyFactory ?? (() => Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var result = await relay.CompleteAsync(
            call,
            new RunnerRelayRequest(logicalModelName, [.. messages], options, this._newKey()),
            cancellationToken);

        if (result.IsCompleted)
        {
            return result.Response!;
        }

        // A hard cap surfaces as the condition the pipeline already handles, so a job stopped by budget
        // finalises as budget-exceeded rather than as a generic failure. Mapping it to anything else here
        // would lose that distinction at the one place it is known.
        if (result.Refusal == RunnerRelayRefusal.BudgetHardCapReached && result.Breach is { } breach)
        {
            throw new BudgetHardCapReachedException(breach);
        }

        throw new RunnerCallRefusedException(nameof(this.GetResponseAsync), result.CallRefusal);
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
}
