// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Provider protocol mode used for a configured model binding.
/// </summary>
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
}
