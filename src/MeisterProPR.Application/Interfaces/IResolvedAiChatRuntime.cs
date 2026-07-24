// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     One resolved chat runtime ready for execution.
/// </summary>
public interface IResolvedAiChatRuntime
{
    /// <summary>Gets the resolved AI connection profile.</summary>
    AiConnectionDto Connection { get; }

    /// <summary>Gets the resolved configured model.</summary>
    AiConfiguredModelDto Model { get; }

    /// <summary>Gets the resolved purpose binding.</summary>
    AiPurposeBindingDto Binding { get; }

    /// <summary>Gets the chat client ready for execution.</summary>
    IChatClient ChatClient { get; }

    /// <summary>Gets runtime capabilities relevant to session-aware review execution.</summary>
    AgentReviewRuntimeCapabilities Capabilities { get; }

    /// <summary>
    ///     Gets the logical-model role this runtime was resolved from, or <see langword="null" /> when it was resolved
    ///     directly from a purpose binding / raw model (no logical model in play). Recorded with usage so token spend
    ///     can be attributed to the logical model that produced it.
    /// </summary>
    string? LogicalModelName { get; }
}
