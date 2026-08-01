// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;

/// <summary>
///     Renders the memory-reconsideration prompts from the shipped Handlebars templates, honouring the client's
///     prompt override for the stage. Stateless.
/// </summary>
public sealed class MemoryReconsiderationPromptBuilder : IMemoryReconsiderationPromptBuilder
{
    /// <inheritdoc />
    public string BuildSystemPrompt(ReviewSystemContext? context)
    {
        return ReviewPrompts.BuildMemoryReconsiderationSystemPrompt(context);
    }

    /// <inheritdoc />
    public string BuildUserMessage(
        string draftFindingsJson,
        IReadOnlyList<ThreadMemoryMatchDto> matches,
        ReviewSystemContext? context = null)
    {
        return ReviewPrompts.BuildMemoryReconsiderationUserMessage(draftFindingsJson, matches, context);
    }
}
