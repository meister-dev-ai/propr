// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
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
}
