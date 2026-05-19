// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One prompt modification targeted at a specific stage and prompt role.
/// </summary>
public sealed record StagePromptVariant(
    string StageKey,
    PromptStageRole PromptRole,
    PromptCompositionMode CompositionMode,
    string Content,
    string? Notes = null);

/// <summary>
///     Prompt role targeted by a prompt experiment variant.
/// </summary>
public enum PromptStageRole
{
    /// <summary>System prompt role.</summary>
    System,

    /// <summary>User prompt role.</summary>
    User,
}

/// <summary>
///     Composition mode used when applying a prompt experiment variant.
/// </summary>
public enum PromptCompositionMode
{
    /// <summary>Default composition mode.</summary>
    Default,

    /// <summary>Replace composition mode.</summary>
    Replace,

    /// <summary>Prepend composition mode.</summary>
    Prepend,

    /// <summary>Append composition mode.</summary>
    Append,
}
