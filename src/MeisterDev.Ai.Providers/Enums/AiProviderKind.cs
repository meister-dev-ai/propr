// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Enums;

/// <summary>
///     Supported AI provider families.
/// </summary>
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
}
