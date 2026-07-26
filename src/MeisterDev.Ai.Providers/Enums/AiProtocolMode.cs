// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Enums;

/// <summary>
///     Provider protocol mode used for a configured model binding.
/// </summary>
/// <remarks>
///     A mode names the request and response shape, not the vendor: several vendors speak
///     <see cref="ChatCompletions" />, and one vendor can offer more than one shape.
///     <see cref="Auto" /> lets the driver pick, which is what a binding should normally say — an explicit mode
///     is for the cases where an operator knows their endpoint only implements one of them.
/// </remarks>
public enum AiProtocolMode
{
    /// <summary>Let the driver choose the best protocol automatically.</summary>
    Auto = 0,

    /// <summary>Use the OpenAI Responses API shape.</summary>
    Responses = 1,

    /// <summary>Use the chat completions API shape.</summary>
    ChatCompletions = 2,

    /// <summary>Use the embeddings API shape.</summary>
    Embeddings = 3,

    /// <summary>Use Anthropic's native Messages API shape.</summary>
    AnthropicMessages = 4,

    /// <summary>Use the AWS Bedrock Converse API shape.</summary>
    BedrockConverse = 5,

    /// <summary>Use Google's generateContent API shape, as served by both Gemini and Vertex AI.</summary>
    GoogleGenerateContent = 6,
}
