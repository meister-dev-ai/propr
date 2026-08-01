// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterDev.Ai.Providers.Catalog;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Runtime;

/// <summary>
///     Keeps a sampling temperature away from a model that will not accept one. Reasoning models on the OpenAI
///     family reject the whole request rather than ignoring the parameter: "Unsupported parameter: 'temperature'
///     is not supported with this model", HTTP 400, on every call a review makes.
/// </summary>
/// <remarks>
///     <para>
///         This belongs to the runtime pipeline rather than to any one caller. A temperature is set wherever a
///         request is built, and the review loop, the synthesis pass and every other stage each build their own,
///         so a rule applied at one call site is a rule the next call site will miss.
///     </para>
///     <para>
///         It learns from the provider rather than from configuration. Whether a model reasons is recorded on the
///         configured model, but that flag defaults to false and was for a long time impossible to set, so keying
///         the rule on it left every existing installation still failing. The provider is the only party that
///         reliably knows, and it says so precisely: the first rejection names the parameter, and every later call
///         on this client omits it. A model whose capability is recorded correctly never pays that first
///         rejection, because the parameter is dropped before the call is made.
///     </para>
///     <para>
///         Scoped to the OpenAI family on purpose. Other vendors accept a temperature from a reasoning-capable
///         model and object only once thinking is actually switched on, which their own clients already handle,
///         so pre-emptively clearing it for them would discard a setting the operator chose.
///     </para>
/// </remarks>
public sealed class ReasoningModelSamplingDecorator : IProviderChatClientDecorator
{
    /// <summary>
    ///     Models the bundled snapshot says reason, so a first call is not spent discovering what is already
    ///     known. Parsed once from the embedded resource; an unreadable snapshot simply teaches nothing.
    /// </summary>
    private static readonly Lazy<HashSet<string>> KnownReasoningModels = new(
        LoadKnownReasoningModels,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     Refusals already seen, shared across every client in the process so one model is never probed twice.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> RefusedModels = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Normalization;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        if (!IsOpenAiFamily(endpoint.ProviderKind))
        {
            return inner;
        }

        var key = $"{endpoint.ProviderKind}|{model.RemoteModelId}";
        var known = model.SupportsReasoning
                    || KnownReasoningModels.Value.Contains(model.RemoteModelId)
                    || RefusedModels.ContainsKey(key);

        return new TemperatureAdaptiveChatClient(inner, known, key);
    }

    private static HashSet<string> LoadKnownReasoningModels()
    {
        try
        {
            using var snapshot = BundledCatalogSnapshot.Open();
            var entries = new ModelsDevCatalogSnapshotImporter().ImportAsync(snapshot).GetAwaiter().GetResult();
            return entries
                .Where(entry => entry.SupportsReasoning)
                .Select(entry => entry.RemoteModelId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // The snapshot is an optimisation, not a source of truth. If it cannot be read the provider still
            // teaches the rule on its first refusal.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsOpenAiFamily(AiProviderKind providerKind)
    {
        return providerKind is AiProviderKind.OpenAi
            or AiProviderKind.AzureOpenAi
            or AiProviderKind.OpenAiCompatible
            or AiProviderKind.LiteLlm;
    }

    /// <summary>Determines whether <paramref name="exception" /> is the provider refusing a temperature.</summary>
    /// <remarks>
    ///     Matched on the message because the refusal is a plain bad request: the status alone cannot distinguish
    ///     it from the many other reasons a request is rejected, and re-sending those without a temperature would
    ///     turn one clear failure into two. Both words must appear, so a message that merely mentions a
    ///     temperature is not mistaken for this.
    /// </remarks>
    public static bool IsTemperatureRefusal(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("temperature", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class TemperatureAdaptiveChatClient(IChatClient inner, bool knownToReason, string modelKey)
        : DelegatingChatClient(inner)
    {
        private volatile bool _refused = knownToReason;

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (this._refused)
            {
                return await base.GetResponseAsync(messages, WithoutTemperature(options), cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (options?.Temperature is not null && IsTemperatureRefusal(ex))
            {
                // Remembered for the process, not just this client, so a model is probed once rather than once
                // per runtime the resolver hands out.
                this._refused = true;
                RefusedModels[modelKey] = true;
                return await base.GetResponseAsync(messages, WithoutTemperature(options), cancellationToken).ConfigureAwait(false);
            }
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // A stream cannot be retried once it has begun, so only what has already been learned is applied.
            return this._refused
                ? base.GetStreamingResponseAsync(messages, WithoutTemperature(options), cancellationToken)
                : base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        // The caller's options are cloned rather than mutated: they are frequently reused across the turns of one
        // loop, and a decorator that edited them would be changing state its caller still owns.
        private static ChatOptions? WithoutTemperature(ChatOptions? options)
        {
            if (options?.Temperature is null)
            {
                return options;
            }

            var clone = options.Clone();
            clone.Temperature = null;
            return clone;
        }
    }
}
