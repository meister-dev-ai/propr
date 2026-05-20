// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptsTemplateBackedLegacyPromptTests
{
    [Fact]
    public void BuildMemoryReconsiderationSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildMemoryReconsiderationSystemPrompt("meister-bot");

        Assert.Contains("You are meister-bot", prompt, StringComparison.Ordinal);
        Assert.Contains("RECONSIDERATION phase", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confidence_evaluations", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMemoryReconsiderationUserMessage_UsesTemplateBackedDefault()
    {
        var message = ReviewPrompts.BuildMemoryReconsiderationUserMessage(
            "{\"summary\":\"Draft\"}",
            [new ThreadMemoryMatchDto(Guid.NewGuid(), 42, "src/Foo.cs", "Previously accepted by design.", 0.92f)]);

        Assert.Contains("## Draft Findings from Initial Review", message, StringComparison.Ordinal);
        Assert.Contains("Match 1", message, StringComparison.Ordinal);
        Assert.Contains("Previously accepted by design.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserMessage_WithChangedFiles_UsesTemplateBackedDefault()
    {
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            "PR description",
            "feature/x",
            "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "class Foo {}", "@@ -1 +1 @@\n-class FooOld {}\n+class Foo {}")],
            PrStatus.Active,
            []);

        var message = ReviewPrompts.BuildUserMessage(pr);

        Assert.Contains("Pull Request: Test PR", message, StringComparison.Ordinal);
        Assert.Contains("======================================= FULL CONTENT =======================================", message, StringComparison.Ordinal);
        Assert.Contains("======================================= DIFF =======================================", message, StringComparison.Ordinal);
    }
}
