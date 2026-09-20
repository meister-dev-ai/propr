// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     The chat client and the embedding generator a family returns when the host supplied no transport. Both
///     are built without one and refuse when a call is made on them.
/// </summary>
/// <remarks>
///     <para>
///         A host builds a client to describe a family as well as to call it, and the driver checks build one
///         against an endpoint that belongs to no connection, so construction has to succeed with no transport
///         available. A family whose vendor client library takes its transport at construction returns these
///         from <see cref="IAiProviderDriver.CreateChatClient" /> and
///         <see cref="IAiProviderDriver.CreateEmbeddingGenerator" /> in that case. A family that takes its
///         transport on the first call has no use for them, because it can already be constructed without one.
///     </para>
///     <para>
///         These refuse instead of falling back to the vendor library's own transport. A client built on that
///         transport would not carry the host's connect-time address check, so it would work in every
///         functional test while losing the control that defends against an operator-supplied base URL.
///     </para>
/// </remarks>
public static class UnreachableClients
{
    private const string RefusalTail =
        " connection was handed no client factory, and a provider family reaches the network only through the "
        + "one the host supplies.";

    /// <summary>What a refusal says when the host supplied no way to reach the network.</summary>
    /// <param name="familyName">
    ///     How the family names itself to an operator, such as <c>Azure OpenAI</c>. Omit it, or pass null or
    ///     blank, for a family that does not name itself in the message.
    /// </param>
    /// <returns>The message to refuse with.</returns>
    public static string Refusal(string? familyName = null)
    {
        return string.IsNullOrWhiteSpace(familyName)
            ? "This" + RefusalTail
            : "This " + familyName.Trim() + RefusalTail;
    }

    /// <summary>The chat client to return when the host supplied no transport.</summary>
    /// <param name="familyName">The name to put in the refusal, as on <see cref="Refusal" />.</param>
    /// <returns>A client whose every call throws <see cref="InvalidOperationException" />.</returns>
    public static IChatClient ChatClient(string? familyName = null)
    {
        return new UnreachableChatClient(Refusal(familyName));
    }

    /// <summary>The embedding generator to return when the host supplied no transport.</summary>
    /// <param name="familyName">The name to put in the refusal, as on <see cref="Refusal" />.</param>
    /// <returns>A generator whose every call throws <see cref="InvalidOperationException" />.</returns>
    public static IEmbeddingGenerator<string, Embedding<float>> EmbeddingGenerator(string? familyName = null)
    {
        return new UnreachableEmbeddingGenerator(Refusal(familyName));
    }

    private sealed class UnreachableChatClient(string refusal) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(refusal);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(refusal);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            // No transport was opened, so there is nothing to release.
        }
    }

    private sealed class UnreachableEmbeddingGenerator(string refusal)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(refusal);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            // No transport was opened, so there is nothing to release.
        }
    }
}
