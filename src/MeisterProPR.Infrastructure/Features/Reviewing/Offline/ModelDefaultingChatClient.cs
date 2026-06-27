// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Wraps a shared chat client so requests that do not specify a model default to a fixed deployment.
///     The offline endpoint serves every tier model and selects the deployment from <see cref="ChatOptions.ModelId" />
///     per request. Most review calls set that explicitly, but some purpose calls (notably triage) pass empty
///     options; this decorator fills the deployment for them so each resolved purpose reaches its configured model.
/// </summary>
internal sealed class ModelDefaultingChatClient(IChatClient innerClient, string modelId) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(messages, this.WithDefaultModel(options), cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetStreamingResponseAsync(messages, this.WithDefaultModel(options), cancellationToken);
    }

    private ChatOptions WithDefaultModel(ChatOptions? options)
    {
        if (options is null)
        {
            return new ChatOptions { ModelId = modelId };
        }

        if (!string.IsNullOrEmpty(options.ModelId))
        {
            return options;
        }

        var clone = options.Clone();
        clone.ModelId = modelId;
        return clone;
    }
}
