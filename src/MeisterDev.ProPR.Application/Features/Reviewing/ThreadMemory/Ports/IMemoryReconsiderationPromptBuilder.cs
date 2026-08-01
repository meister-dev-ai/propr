// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.ThreadMemory.Ports;

/// <summary>
///     Builds the two messages the memory-augmented reconsideration call sends. The implementation renders the
///     shipped prompt templates and applies the client's prompt override for the stage, so reconsideration is
///     inspectable and overridable like every other review stage. This is the only source of reconsideration
///     prompts; a rendering failure surfaces as an exception and the caller falls back to keeping the draft
///     findings unchanged.
/// </summary>
public interface IMemoryReconsiderationPromptBuilder
{
    /// <summary>
    ///     Builds the reconsideration system prompt. When the review context carries a
    ///     <c>MemoryReconsiderationSystemPrompt</c> override, the override text replaces the shipped template.
    /// </summary>
    /// <param name="context">The review execution context, or <see langword="null" /> for the shipped default.</param>
    string BuildSystemPrompt(ReviewSystemContext? context);

    /// <summary>
    ///     Builds the reconsideration user message from the draft findings and the historical memory matches.
    /// </summary>
    /// <param name="draftFindingsJson">The draft findings serialized as JSON.</param>
    /// <param name="matches">The retrieved historical memory matches, in the order they should be presented.</param>
    /// <param name="context">The review execution context, or <see langword="null" /> for the shipped default.</param>
    string BuildUserMessage(
        string draftFindingsJson,
        IReadOnlyList<ThreadMemoryMatchDto> matches,
        ReviewSystemContext? context = null);
}
