// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Default implementation of <see cref="IResolvedAiChatRuntime" />.
/// </summary>
public sealed record ResolvedAiChatRuntime(
    AiConnectionDto Connection,
    AiConfiguredModelDto Model,
    AiPurposeBindingDto Binding,
    IChatClient ChatClient) : IResolvedAiChatRuntime;
