// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

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
}
