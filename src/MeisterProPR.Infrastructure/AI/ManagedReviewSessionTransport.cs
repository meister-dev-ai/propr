// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.ObjectModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

internal interface IManagedReviewSessionTransportFactory
{
    IManagedReviewSessionTransport Create(IChatClient chatClient, IReadOnlyList<AIFunction> tools);
}

internal interface IManagedReviewSessionTransport
{
    Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken = default);
}

internal sealed class ManagedReviewSessionTransportFactory : IManagedReviewSessionTransportFactory
{
    public IManagedReviewSessionTransport Create(IChatClient chatClient, IReadOnlyList<AIFunction> tools)
    {
        return new AgentFrameworkReviewSessionTransport(chatClient, tools);
    }
}

internal sealed class AgentFrameworkReviewSessionTransport : IManagedReviewSessionTransport
{
    private readonly ChatClientAgent agent;
    private readonly RunObservingChatClient chatClient;
    private AgentSession? session;

    public AgentFrameworkReviewSessionTransport(IChatClient chatClient, IReadOnlyList<AIFunction> tools)
    {
        this.chatClient = new RunObservingChatClient(chatClient);
        this.agent = new ChatClientAgent(this.chatClient, null, null, null, [.. tools]);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken = default)
    {
        var observedRun = this.chatClient.BeginRun();

        try
        {
            this.session ??= !string.IsNullOrWhiteSpace(options.ConversationId)
                ? await this.agent.CreateSessionAsync(options.ConversationId!, cancellationToken)
                : await this.agent.CreateSessionAsync(cancellationToken);

            var agentResponse = await this.agent.RunAsync(
                messages, this.session,
                new ChatClientAgentRunOptions
                {
                    ChatOptions = options,
                    ContinuationToken = options.ContinuationToken,
                },
                cancellationToken);

            return new ChatResponse(agentResponse.Messages)
            {
                AdditionalProperties = agentResponse.AdditionalProperties,
                ContinuationToken = agentResponse.ContinuationToken,
                ConversationId = (this.session as ChatClientAgentSession)?.ConversationId,
                CreatedAt = agentResponse.CreatedAt,
                FinishReason = agentResponse.FinishReason,
                ModelId = options.ModelId,
                RawRepresentation = agentResponse.RawRepresentation,
                ResponseId = agentResponse.ResponseId,
                Usage = agentResponse.Usage,
            };
        }
        catch (Exception ex)
        {
            throw ManagedReviewSessionTransportException.Create(ex, observedRun);
        }
        finally
        {
            this.chatClient.EndRun(observedRun);
        }
    }
}

internal sealed class ManagedReviewSessionTransportException : Exception
{
    private ManagedReviewSessionTransportException(
        string message,
        Exception innerException,
        bool continuationStarted,
        string? providerConversationId,
        IReadOnlyList<ChatMessage> recoveredMessages)
        : base(message, innerException)
    {
        this.ContinuationStarted = continuationStarted;
        this.ProviderConversationId = providerConversationId;
        this.RecoveredMessages = recoveredMessages;
    }

    public bool ContinuationStarted { get; }

    public string? ProviderConversationId { get; }

    public IReadOnlyList<ChatMessage> RecoveredMessages { get; }

    public static ManagedReviewSessionTransportException Create(Exception innerException, ObservedManagedSessionRun observedRun)
    {
        return new ManagedReviewSessionTransportException(
            innerException.Message,
            innerException,
            observedRun.ContinuationStarted,
            observedRun.ProviderConversationId,
            observedRun.BuildRecoveredMessages());
    }
}

internal sealed class RunObservingChatClient(IChatClient inner) : IChatClient
{
    private ObservedManagedSessionRun? currentRun;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages is IList<ChatMessage> list ? list.ToList() : messages.ToList();
        this.currentRun?.RecordRequest(messageList, options);

        var response = await inner.GetResponseAsync(messageList, options, cancellationToken);
        this.currentRun?.RecordResponse(response);
        return response;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return inner.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        inner.Dispose();
    }

    public ObservedManagedSessionRun BeginRun()
    {
        var run = new ObservedManagedSessionRun();
        this.currentRun = run;
        return run;
    }

    public void EndRun(ObservedManagedSessionRun run)
    {
        if (ReferenceEquals(this.currentRun, run))
        {
            this.currentRun = null;
        }
    }
}

internal sealed class ObservedManagedSessionRun
{
    private readonly List<ChatMessage> firstRequestMessages = [];
    private readonly List<ChatMessage> lastRequestMessages = [];
    private readonly List<ChatMessage> successfulResponseMessages = [];

    public int RequestCount { get; private set; }

    public string? ProviderConversationId { get; private set; }

    public bool ContinuationStarted => this.RequestCount > 1 || !string.IsNullOrWhiteSpace(this.ProviderConversationId);

    public void RecordRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        this.RequestCount++;

        if (this.RequestCount == 1)
        {
            this.firstRequestMessages.Clear();
            this.firstRequestMessages.AddRange(messages);
        }

        this.lastRequestMessages.Clear();
        this.lastRequestMessages.AddRange(messages);
        this.ProviderConversationId ??= options?.ConversationId;
    }

    public void RecordResponse(ChatResponse response)
    {
        this.successfulResponseMessages.AddRange(response.Messages);
        this.ProviderConversationId ??= response.ConversationId;
    }

    public IReadOnlyList<ChatMessage> BuildRecoveredMessages()
    {
        var recoveredMessages = new List<ChatMessage>();
        recoveredMessages.AddRange(this.successfulResponseMessages);

        var recoverPendingToolResults = this.RequestCount > 1 && this.firstRequestMessages.Any(message => message.Role == ChatRole.User) &&
                                        this.lastRequestMessages.All(message => message.Role == ChatRole.Tool);
        if (recoverPendingToolResults)
        {
            recoveredMessages.AddRange(this.lastRequestMessages);
        }

        return new ReadOnlyCollection<ChatMessage>(recoveredMessages);
    }
}
