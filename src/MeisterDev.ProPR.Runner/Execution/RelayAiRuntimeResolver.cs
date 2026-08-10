// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Which model each stage of the review runs on, on a host with no connection repository.
///     <para>
///         The pipeline asks this for a purpose — the per-file tier, triage, verification — and uses the
///         answer for four things: the client to call, the model id it records, the tokenizer it counts
///         prompts with, and the context window it budgets against. Without a resolver every one of those
///         comes back null, and the review still runs — on the default client, blind. That is how a remote
///         review recorded real token totals against a protocol that named no model, and shipped no spend
///         at all.
///     </para>
///     <para>
///         Every purpose resolves to the manifest's default model. The manifest carries one default plus a
///         named binding per configured pass, and a pass is resolved by name through the logical-model
///         resolver rather than by purpose — so there is nothing else here to choose between. A client that
///         wants a different model per tier configures it as a pass.
///     </para>
/// </summary>
public sealed class RelayAiRuntimeResolver(RunnerJobManifest manifest, Func<string, IChatClient> relay) : IAiRuntimeResolver
{
    /// <inheritdoc />
    public Task<IResolvedAiChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default)
    {
        return Task.FromResult<IResolvedAiChatRuntime>(this.DefaultRuntime());
    }

    /// <inheritdoc />
    public Task<IResolvedAiChatRuntime> ResolveChatRuntimeForModelAsync(
        Guid clientId,
        Guid configuredModelId,
        CancellationToken ct = default)
    {
        // A configured-model id is a database key, and the manifest deliberately carries names rather than
        // keys: resolving one is the control plane's job. A pass that binds a model this way is refused at
        // dispatch, so reaching here means the pipeline asked for something this review was not given.
        throw new NotSupportedException(
            "A runner resolves models by name, not by configured-model id. A pass that binds a configured "
            + "model directly cannot be executed out of process.");
    }

    /// <inheritdoc />
    public Task<IResolvedAiEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        // Everything that embeds — deduplication and the semantic screener — runs where findings are
        // published. A stub returning zeros would corrupt every similarity comparison downstream of it.
        throw new NotSupportedException("A runner relays chat completions; embeddings are computed in the control plane.");
    }

    private RelayChatRuntime DefaultRuntime()
    {
        var binding = manifest.DefaultModel;
        return new RelayChatRuntime(binding, relay(binding.LogicalModelName));
    }
}
