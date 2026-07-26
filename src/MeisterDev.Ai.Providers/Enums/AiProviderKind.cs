// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Enums;

/// <summary>
///     AI provider families the system can describe.
/// </summary>
/// <remarks>
///     A family listed here is one the system can name, store and reason about — not necessarily one this build
///     can call. Which families an operator is offered comes from the drivers that are actually registered
///     (<see cref="Drivers.IAiProviderDriverRegistry.RegisteredKinds" />), so a family can be added here ahead of
///     its driver without creating a profile that fails only when a review runs. Values persist as names rather
///     than numbers, so the numeric order carries no meaning.
/// </remarks>
public enum AiProviderKind
{
    /// <summary>Azure OpenAI or Azure AI Foundry.</summary>
    AzureOpenAi = 0,

    /// <summary>OpenAI-hosted APIs.</summary>
    OpenAi = 1,

    /// <summary>LiteLLM OpenAI-compatible gateways.</summary>
    LiteLlm = 2,

    /// <summary>
    ///     Any OpenAI-compatible endpoint reached at an operator-supplied base URL: a vendor's own API
    ///     (DeepSeek, Alibaba Qwen, Moonshot Kimi, MiniMax, xAI, Mistral, Groq, Together, Fireworks), an
    ///     aggregator such as OpenRouter, or a self-hosted server such as Ollama or vLLM. Kept distinct from
    ///     <see cref="LiteLlm" /> so config, telemetry, and per-provider usage keys can tell a proxy apart from
    ///     a direct endpoint even though both speak the same protocol.
    /// </summary>
    OpenAiCompatible = 3,

    /// <summary>
    ///     Anthropic's own Messages API. Distinct from reaching Claude through an OpenAI-compatible gateway:
    ///     the native API is where cache-control breakpoints and its reasoning shape are expressible at all.
    /// </summary>
    Anthropic = 4,

    /// <summary>
    ///     AWS Bedrock. Its own protocol and SigV4 auth, and the only route to a model that is available in one
    ///     AWS region and nowhere else.
    /// </summary>
    AwsBedrock = 5,

    /// <summary>
    ///     Google Gemini and Vertex AI. Two front doors to the same models — a public API key endpoint and a
    ///     project-scoped Vertex one — sharing a protocol and separated by auth and project pinning.
    /// </summary>
    GoogleVertex = 6,
}
