// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Default implementation of <see cref="IResolvedAiChatRuntime" />.
/// </summary>
public sealed record ResolvedAiChatRuntime(
    AiConnectionDto Connection,
    AiConfiguredModelDto Model,
    AiPurposeBindingDto Binding,
    IChatClient ChatClient,
    AgentReviewRuntimeCapabilities Capabilities) : IResolvedAiChatRuntime
{
    /// <inheritdoc />
    public string? LogicalModelName { get; init; }
}
