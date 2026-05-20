// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptsTemplateBackedAgenticTests
{
    [Fact]
    public void BuildAgenticFilePlanningUserMessage_UsesTemplateBackedDefault()
    {
        var currentFile = new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+services.AddFoo();");
        var siblingFile = new ChangedFile("src/Bar.cs", ChangeType.Edit, "code", "+services.AddBar();");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Refactor registration",
            "Touches startup and tests.",
            "feature/foo",
            "main",
            [currentFile, siblingFile]);

        var message = ReviewPrompts.BuildAgenticFilePlanningUserMessage(currentFile, pr);

        Assert.Contains("Anchor file: src/Foo.cs", message, StringComparison.Ordinal);
        Assert.Contains("Changed file manifest:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAgenticFileInvestigationUserMessage_UsesTemplateBackedDefault()
    {
        var plan = new AgenticFileReviewPlan(
            "plan-1",
            "src/Foo.cs",
            ["Check wiring"],
            ["src/Foo.cs"],
            [new AgenticFileInvestigationTask("task-1", "concern", "configuration_or_wiring", "Check wiring", ["src/Foo.cs"], ["get_file_content"], 2)]);
        var task = plan.InvestigationTasks[0];
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Refactor registration",
            null,
            "feature/foo",
            "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+services.AddFoo();")]);

        var message = ReviewPrompts.BuildAgenticFileInvestigationUserMessage(plan, task, pr);

        Assert.Contains("Plan ID: plan-1", message, StringComparison.Ordinal);
        Assert.Contains("Allowed tools: get_file_content", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPerFileUserMessage_UsesTemplateBackedDefault()
    {
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+code");
        var summaries = new List<ChangedFileSummary> { new(file.Path, file.ChangeType) }.AsReadOnly();

        var message = ReviewPrompts.BuildPerFileUserMessage(file, 1, 1, summaries, [], "My PR", "feature/x", "main");

        Assert.Contains("Reviewing file 1 of 1: src/Foo.cs", message, StringComparison.Ordinal);
        Assert.Contains("======================================= DIFF =======================================", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPerFileContextPrompt_UsesTemplateBackedDefault()
    {
        var context = new ReviewSystemContext(null, [], null)
        {
            PerFileHint = new PerFileReviewHint(
                "src/Foo.cs", 1, 2, [new ChangedFileSummary("src/Foo.cs", ChangeType.Edit), new ChangedFileSummary("src/Bar.cs", ChangeType.Edit)]),
        };

        var prompt = ReviewPrompts.BuildPerFileContextPrompt(context, "src/Foo.cs", 1, 2);

        Assert.Contains("You are reviewing **src/Foo.cs** (1 of 2)", prompt, StringComparison.Ordinal);
        Assert.Contains("Mandatory investigation requirement", prompt, StringComparison.Ordinal);
    }
}
