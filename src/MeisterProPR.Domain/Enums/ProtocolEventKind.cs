// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Discriminates the kind of event recorded in a <see cref="MeisterProPR.Domain.Entities.ProtocolEvent" />.</summary>
public enum ProtocolEventKind
{
    /// <summary>A single call to the AI model (one <c>GetResponseAsync</c> invocation).</summary>
    AiCall = 0,

    /// <summary>A single tool invocation requested by the AI during the review loop.</summary>
    ToolCall = 1,

    /// <summary>A memory-system operation (store, remove, retrieve, reconsider, or failure).</summary>
    MemoryOperation = 2,
}
