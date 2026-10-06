// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptsTemplateBackedSharedTests
{
    [Fact]
    public void BuildGlobalSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(null);

        Assert.Contains("expert code reviewer specialising in general software engineering best practices", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CERTAINTY GATE", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The very first character must be '{'", prompt, StringComparison.Ordinal);
        Assert.Contains("confidence_evaluations", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSynthesisSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(null, true);

        Assert.Contains("cross_cutting_concerns", prompt, StringComparison.Ordinal);
        Assert.Contains("The very first character must be '{'", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQualityFilterSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildQualityFilterSystemPrompt(null);

        Assert.Contains("senior code review editor", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISCARD", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
