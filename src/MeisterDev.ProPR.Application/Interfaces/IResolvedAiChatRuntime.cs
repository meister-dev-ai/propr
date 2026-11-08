// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Interfaces;

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

    /// <summary>Whether the model and selected transport positively support low reasoning effort.</summary>
    bool SupportsLowReasoningEffort => false;

    /// <summary>Gets runtime capabilities relevant to session-aware review execution.</summary>
    AgentReviewRuntimeCapabilities Capabilities { get; }

    /// <summary>
    ///     Gets the logical-model role this runtime was resolved from, or <see langword="null" /> when it was resolved
    ///     directly from a purpose binding / raw model (no logical model in play). Recorded with usage so token spend
    ///     can be attributed to the logical model that produced it.
    /// </summary>
    string? LogicalModelName { get; }

    /// <summary>
    ///     Gets the reasoning effort of the logical model this runtime was resolved from, or
    ///     <see cref="ReviewReasoningEffort.None" /> when it was resolved from a purpose binding or a raw model. A review
    ///     pass whose model comes from a logical model with an effort other than None uses that effort instead of the
    ///     client's baseline effort.
    /// </summary>
    ReviewReasoningEffort LogicalModelReasoningEffort => ReviewReasoningEffort.None;
}
