// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Fails with each queued exception in turn — a <see langword="null" /> entry means "succeed this time".
/// </summary>
internal sealed class ScriptedChatClient(IReadOnlyList<Exception?> script, bool failAfterFirstUpdate = false) : IChatClient
{
    public int Calls { get; private set; }

    public List<IList<ChatMessage>> Conversations { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Observed before the call is counted, so a stage that should have stopped at a cancelled token cannot
        // pass a test by reaching a stub that answers anyway.
        cancellationToken.ThrowIfCancellationRequested();

        this.Record(messages);
        var failure = this.NextFailure();
        return failure is not null
            ? Task.FromException<ChatResponse>(failure)
            : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this.Record(messages);
        var failure = this.NextFailure();
        if (failure is not null)
        {
            throw failure;
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");

        if (failAfterFirstUpdate)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "stream cut");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public TService? GetService<TService>(object? key = null)
        where TService : class => null;

    public void Dispose()
    {
    }

    private void Record(IEnumerable<ChatMessage> messages)
    {
        this.Conversations.Add(messages.ToList());
        this.Calls++;
    }

    private Exception? NextFailure()
    {
        var index = this.Calls - 1;
        return index < script.Count ? script[index] : null;
    }
}
